# agent-platform

Temporal workers and webhook intake for homelab agents, in .NET.

One .NET 10 image runs as three roles. The role is chosen by configuration:

| Role      | Does                                                                          | Ports                                                  |
| --------- | ----------------------------------------------------------------------------- | ------------------------------------------------------ |
| `Web`     | ASP.NET minimal API (health endpoints) and AgentGateway, the MCP server       | `8080` webhooks, `8081` MCP, set by the Web deployment |
| `Worker`  | Temporal workers on task queues `agent-platform` (workflows) and `agent-pods` | `8080` health only                                     |
| `Migrate` | Applies the run-data database migrations, then exits                          | none                                                   |

Both roles serve `/health` (all checks) and `/alive` (liveness) through `AgentPlatform.ServiceDefaults`, which also wires OpenTelemetry and HTTP resilience.

## Configuration

| Key                                       | Default                                                               | Notes                                                                                                                                                       |
| ----------------------------------------- | --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AgentPlatform:Role`                      | none, required                                                        | `Web`, `Worker` or `Migrate`; startup fails on anything else                                                                                                |
| `Temporal:Address`                        | `localhost:7233`                                                      | Both roles                                                                                                                                                  |
| `Temporal:Namespace`                      | `default`                                                             | Both roles                                                                                                                                                  |
| `AgentGateway:SessionTokenKey`            | none, required                                                        | At least 32 characters, identical on both roles; signs the per-run MCP session tokens, which expire shortly after the pod deadline                          |
| `AgentGateway:Port`                       | `8081`                                                                | Web role. The gateway port serves only `/mcp`, and `/mcp` answers only there; startup fails when it is outside 1-65535 or equals `AgentGateway:WebhookPort` |
| `AgentGateway:WebhookPort`                | `8080`                                                                | Web role. The port that serves health and webhooks; must match the Web deployment                                                                           |
| `AgentPods:MaxConcurrentActivities`       | `2`                                                                   | Worker role. The global cap on concurrent agent pods                                                                                                        |
| `AgentPods:HeartbeatInterval`             | `00:00:15`                                                            | Worker role. Must be less than half of the 60 second heartbeat timeout of the `RunAgentPod` activity                                                        |
| `AgentPods:Namespace`                     | `agent-platform-agents`                                               | Worker role                                                                                                                                                 |
| `AgentPods:Image`                         | pinned `claude-agent-read` tag and digest in `AgentPodsOptions`       | Worker role. Bump the default when the image is updated                                                                                                     |
| `AgentPods:Model`                         | `sonnet`                                                              | Worker role. Must be allowed by the tier's LiteLLM key                                                                                                      |
| `AgentPods:LiteLlmBaseUrl`                | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Injected into agent pods as `ANTHROPIC_BASE_URL`                                                                                               |
| `AgentPods:AgentGatewayUrl`               | `http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp` | Worker role. The MCP endpoint written into agent pods                                                                                                       |
| `AgentPods:ServiceAccountName`            | `agent-runner`                                                        | Worker role                                                                                                                                                 |
| `AgentPods:ClusterReadServiceAccountName` | unset                                                                 | Worker role. Required to run the `cluster-read` tier                                                                                                        |
| `ConnectionStrings:rundata`               | none, required                                                        | Worker and Migrate roles. The Npgsql connection string of the run-data database                                                                             |
| `OTEL_EXPORTER_OTLP_ENDPOINT`             | unset                                                                 | Enables the OTLP exporter when set                                                                                                                          |
| `ASPNETCORE_HTTP_PORTS`                   | `8080`                                                                | Image default; the Web deployment sets `8080;8081`                                                                                                          |

Environment variables use `__` as the separator, for example `AgentPlatform__Role=Worker`.

In the cluster the run-data connection string uses `SSL Mode=VerifyFull` with `Root Certificate=/etc/agent-platform/rundata-ca/ca.crt`, the mounted CA of the database cluster.

## Agent pods and the spike workflow

`SpikeWorkflow` proves the agent-pod loop end to end.

1. The workflow runs the `RunAgentPod` activity on task queue `agent-pods`.
2. The activity (`KubernetesAgentRunner`) creates a Claude Code pod in `AgentPods:Namespace` and then a per-run Secret owned by the pod. It watches the pod to completion, heartbeating. The workflow then runs the `DeleteAgentPod` activity as a local activity, also when the run activity fails, so cleanup never waits behind long runs on the `agent-pods` queue.
3. The agent calls the `submit_result` tool of AgentGateway. The gateway checks the per-run session token and sends the result to the waiting workflow as a Temporal Update. The workflow accepts the first result and rejects any further one, so a repeated `submit_result` call gets an error.
4. The workflow returns the submitted result with the run metadata (`Status`, `Result`, `Agent`, `Tier`, `WorkflowId`, `RunId`).

Pod names are derived from the workflow ID, run ID and step, so a retried activity finds its pod with `AlreadyExists` and re-attaches instead of creating a second one. On every attempt the runner replaces the per-run Secret (delete, then create) so the current pod owns it; the runner's Role needs only `create` and `delete` on Secrets. A pod that vanished while the activity waited is reported as
`PodFailed` and is not recreated.

Pod deletion has two owners. The activity deletes the pod only when the workflow cancels it. An attempt that ends any other way (heartbeat timeout, worker shutdown, a transient API failure) leaves the pod for the retry to re-attach to. The workflow's `DeleteAgentPod` removes the pod once the run ends or fails for good, with the pod's `activeDeadlineSeconds` and the cluster's pod cleanup as the
final backstop.

The activity timeout and the pod's `activeDeadlineSeconds` come from the same deadline (`AgentRunTimeouts`); the deadline is at most 3600 seconds. The session token of a run expires 60 seconds after the pod deadline plus the scheduling grace. The runner classifies each run from the pod's `stream-json` stdout: `Succeeded`, `Truncated` (no result event, a non-success subtype, zero output tokens or
an empty result), `UsageLimit`, `PodFailed` or `TimedOut`. Any outcome other than `Succeeded` ends the workflow with status `AgentFailed`.

Start a run with the Temporal CLI in the admin-tools pod, which the frontend already accepts connections from:

```bash
kubectl -n temporal-system exec deploy/temporal-admintools -- temporal workflow start \
  --address temporal-frontend:7233 --namespace default \
  --task-queue agent-platform --type SpikeWorkflow \
  --workflow-id "spike-$(date +%s)" \
  --input '{"prompt":"Reply with a one line greeting, then call the submit_result tool.","tier":"read"}'
```

`tier` is `read` or `cluster-read`; the `cluster-read` tier needs `AgentPods:ClusterReadServiceAccountName`. Read the outcome with `temporal workflow show --address temporal-frontend:7233 --workflow-id <id>` through the same pod, or in the Temporal UI.

## Run data

Every workflow run is recorded in a PostgreSQL database (`rundata`) through EF Core, so runs can be measured, compared and replayed later.

| Table       | One row per     | Key columns                                                                                                                                                                                                          |
| ----------- | --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `runs`      | workflow run    | `workflow_id` + `workflow_run_id` (unique), `workflow_type`, `status`, `input_event` and `context` (jsonb), `prompt_hash`, `model`, timings, token and cost totals, nullable `outcome` fields for later ground truth |
| `run_steps` | step of a run   | `run_id` + `seq` (unique), `name`, `rung` (`Rule`, `Jev`, `Claude`), `input` and `output` (jsonb), `confidence`, `stop_reason`, `prompt_hash`, `model`, tokens, `cost_usd`, timings                                  |
| `prompts`   | distinct prompt | `hash` (SHA-256 of the body, primary key), `name`, `body`                                                                                                                                                            |

Only activities write to the database, so workflow code stays deterministic. `BeginRun`, `RecordStep` and `CompleteRun` run on the `agent-platform` queue and are idempotent on the unique keys above, so a retried activity never duplicates a row. A failed or cancelled run is completed as `Failed`.

Run data is readable through a read-only tool, so it never holds credential material: only prompts, model inputs and outputs, and metrics are stored. `JsonRedactor` replaces the value of every credential-named JSON property (token, secret, password, authorization, cookie, signature, API or private
key) and every credential-shaped string (GitHub, API and cloud keys, bearer values, JWTs and the per-run session tokens) with `[redacted]` in every jsonb column before it is written. Token-count metrics such as `input_tokens` are kept. Callers still pass only the fields they need from an input
event, never raw headers.

The `Migrate` role applies the EF Core migrations in `src/AgentPlatform.Host/RunData/Migrations` and exits `0` on success or non-zero on failure, so the Worker never migrates. Add a migration with the local tool (`dotnet tool restore` first):

```bash
dotnet ef migrations add <Name> --project src/AgentPlatform.Host --output-dir RunData/Migrations
```

CI fails when the model and the migrations differ (`dotnet ef migrations has-pending-model-changes`).

## Layout

| Path                                   | Purpose                                                                                    |
| -------------------------------------- | ------------------------------------------------------------------------------------------ |
| `src/AgentPlatform.Host`               | The executable: role selection, AgentGateway, Temporal workers, agent-pod runner, run data |
| `src/AgentPlatform.ServiceDefaults`    | Shared OpenTelemetry, health checks and resilience                                         |
| `src/AgentPlatform.AppHost`            | Aspire AppHost for local development only                                                  |
| `tests/AgentPlatform.Tests`            | xUnit unit, time-skipping workflow and replay tests                                        |
| `tests/AgentPlatform.IntegrationTests` | Aspire integration tests against the AppHost                                               |

## Run locally

Needs the .NET SDK pinned in `global.json` and a container runtime for the Temporal dev server.

```bash
dotnet run --project src/AgentPlatform.AppHost
```

The AppHost starts a Temporal dev server (`temporalio/temporal`, CLI `1.9.x`, which embeds Temporal server 1.32 like the cluster) in the `agent-platform` namespace, a PostgreSQL container with the `rundata` database, the `Migrate` role (the Worker waits for it to finish) and the Host as Web and
Worker, and opens the Aspire dashboard at `http://localhost:15170`. The dev server UI is at `http://localhost:8233`. With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`.

## Build and test

```bash
dotnet build
dotnet test --project tests/AgentPlatform.Tests
dotnet test --project tests/AgentPlatform.IntegrationTests
dotnet format AgentPlatform.slnx --verify-no-changes
```

The workflow tests run against Temporal's time-skipping test server, which the SDK downloads on first use.

The replay tests (`tests/AgentPlatform.Tests/Replay`) replay the committed histories in `Replay/Histories` against the current workflow code and fail on non-determinism. After an intentional workflow change, re-record them with `RECORD_HISTORIES=1 dotnet test --project tests/AgentPlatform.Tests` and commit the diff; normal runs never rewrite them. The histories are synthetic, recorded by the
time-skipping tests.

The integration tests share one AppHost (Temporal dev server, PostgreSQL, Migrate and both Host roles) through `Aspire.Hosting.Testing`. They run `PingWorkflow` end to end, check its `runs` row and check that the Migrate role finished with exit code 0. They need a container runtime, which GitHub's ubuntu runners have.

## Image

```bash
docker build -t agent-platform .
docker run --rm -e AgentPlatform__Role=Web agent-platform
```

The runtime image is the chiseled Ubuntu ASP.NET image running as UID `1654`. It needs no writable root filesystem, so it runs under Pod Security `restricted` with `readOnlyRootFilesystem`. Releases are cut by release-please and published to GHCR by the shared CI.
