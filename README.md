# agent-platform

Temporal workers and webhook intake for homelab agents, in .NET.

One .NET 10 image runs as two deployments. The role is chosen by configuration:

| Role     | Does                                                                          | Ports                                                  |
| -------- | ----------------------------------------------------------------------------- | ------------------------------------------------------ |
| `Web`    | ASP.NET minimal API (health endpoints) and AgentGateway, the MCP server       | `8080` webhooks, `8081` MCP, set by the Web deployment |
| `Worker` | Temporal workers on task queues `agent-platform` (workflows) and `agent-pods` | `8080` health only                                     |

Both roles serve `/health` (all checks) and `/alive` (liveness) through `AgentPlatform.ServiceDefaults`, which also wires OpenTelemetry and HTTP resilience.

## Configuration

| Key                                       | Default                                                               | Notes                                                                                                            |
| ----------------------------------------- | --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| `AgentPlatform:Role`                      | none, required                                                        | `Web` or `Worker`; startup fails on anything else                                                                |
| `Temporal:Address`                        | `localhost:7233`                                                      | Both roles                                                                                                       |
| `Temporal:Namespace`                      | `default`                                                             | Both roles                                                                                                       |
| `AgentGateway:SessionTokenKey`            | none, required                                                        | At least 32 characters, identical on both roles; signs the per-run MCP session tokens                            |
| `AgentGateway:Port`                       | `8081`                                                                | Web role. The gateway port serves only `/mcp`, and `/mcp` answers only there; `0` disables the check (local dev) |
| `AgentPods:MaxConcurrentActivities`       | `2`                                                                   | Worker role. The global cap on concurrent agent pods                                                             |
| `AgentPods:HeartbeatInterval`             | `00:00:15`                                                            | Worker role. Must be less than half of `AgentPods:HeartbeatTimeout`                                              |
| `AgentPods:HeartbeatTimeout`              | `00:01:00`                                                            | Worker role. The heartbeat timeout of the `RunAgentPod` activity                                                 |
| `AgentPods:Namespace`                     | `agent-platform-agents`                                               | Worker role                                                                                                      |
| `AgentPods:Image`                         | pinned `claude-agent-read` tag and digest in `AgentPodsOptions`       | Worker role. Bump the default when the image is updated                                                          |
| `AgentPods:Model`                         | `sonnet`                                                              | Worker role. Must be allowed by the tier's LiteLLM key                                                           |
| `AgentPods:LiteLlmBaseUrl`                | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Injected into agent pods as `ANTHROPIC_BASE_URL`                                                    |
| `AgentPods:AgentGatewayUrl`               | `http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp` | Worker role. The MCP endpoint written into agent pods                                                            |
| `AgentPods:ServiceAccountName`            | `agent-runner`                                                        | Worker role                                                                                                      |
| `AgentPods:ClusterReadServiceAccountName` | unset                                                                 | Worker role. Required to run the `cluster-read` tier                                                             |
| `OTEL_EXPORTER_OTLP_ENDPOINT`             | unset                                                                 | Enables the OTLP exporter when set                                                                               |
| `ASPNETCORE_HTTP_PORTS`                   | `8080`                                                                | Image default; the Web deployment sets `8080;8081`                                                               |

Environment variables use `__` as the separator, for example `AgentPlatform__Role=Worker`.

## Agent pods and the spike workflow

`SpikeWorkflow` proves the agent-pod loop end to end.

1. The workflow runs the `RunAgentPod` activity on task queue `agent-pods`.
2. The activity (`KubernetesAgentRunner`) creates a Claude Code pod in `AgentPods:Namespace` and then a per-run Secret owned by the pod. It watches the pod to completion, heartbeating. The workflow then runs the `DeleteAgentPod` activity, also when the run activity fails.
3. The agent calls the `submit_result` tool of AgentGateway. The gateway checks the per-run session token and signals the waiting workflow.
4. The workflow returns the signalled result with the run metadata (`Status`, `Result`, `Agent`, `Tier`, `WorkflowId`, `RunId`).

Pod names are derived from the workflow ID, run ID and step, so a retried activity finds its pod with `AlreadyExists` and re-attaches instead of creating a second one. A pod is deleted by the workflow after the run activity ends (or fails for good) or when the workflow cancels the activity; a transient failure or a worker shutdown leaves it for the retry. A pod that vanished while the activity
waited is reported as `PodFailed` and is not recreated.

The activity timeout and the pod's `activeDeadlineSeconds` come from the same deadline (`AgentRunTimeouts`); the deadline is at most 3600 seconds. The runner classifies each run from the pod's `stream-json` stdout: `Succeeded`, `Truncated` (no result event, a non-success subtype, zero output tokens or an empty result), `UsageLimit`, `PodFailed` or `TimedOut`. Any outcome other than `Succeeded`
ends the workflow with status `AgentFailed`.

Start a run with the Temporal CLI in the admin-tools pod, which the frontend already accepts connections from:

```bash
kubectl -n temporal-system exec deploy/temporal-admintools -- temporal workflow start \
  --address temporal-frontend:7233 --namespace default \
  --task-queue agent-platform --type SpikeWorkflow \
  --workflow-id "spike-$(date +%s)" \
  --input '{"prompt":"Reply with a one line greeting, then call the submit_result tool.","tier":"read"}'
```

`tier` is `read` or `cluster-read`; the `cluster-read` tier needs `AgentPods:ClusterReadServiceAccountName`. Read the outcome with `temporal workflow show --address temporal-frontend:7233 --workflow-id <id>` through the same pod, or in the Temporal UI.

## Layout

| Path                                | Purpose                                                                          |
| ----------------------------------- | -------------------------------------------------------------------------------- |
| `src/AgentPlatform.Host`            | The executable: role selection, AgentGateway, Temporal workers, agent-pod runner |
| `src/AgentPlatform.ServiceDefaults` | Shared OpenTelemetry, health checks and resilience                               |
| `src/AgentPlatform.AppHost`         | Aspire AppHost for local development only                                        |
| `tests/AgentPlatform.Tests`         | xUnit tests                                                                      |

## Run locally

Needs the .NET SDK pinned in `global.json` and a container runtime for the Temporal dev server.

```bash
dotnet run --project src/AgentPlatform.AppHost
```

The AppHost starts a Temporal dev server (`temporalio/temporal`, CLI `1.9.x`, which embeds Temporal server 1.32 like the cluster) plus the Host twice, once per role, and opens the Aspire dashboard at `http://localhost:15170`. The dev server UI is at `http://localhost:8233`. With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`.

## Build and test

```bash
dotnet build
dotnet test --solution AgentPlatform.slnx
dotnet format AgentPlatform.slnx --verify-no-changes
```

The workflow test runs against Temporal's time-skipping test server, which the SDK downloads on first use.

## Image

```bash
docker build -t agent-platform .
docker run --rm -e AgentPlatform__Role=Web agent-platform
```

The runtime image is the chiseled Ubuntu ASP.NET image running as UID `1654`. It needs no writable root filesystem, so it runs under Pod Security `restricted` with `readOnlyRootFilesystem`. Releases are cut by release-please and published to GHCR by the shared CI.
