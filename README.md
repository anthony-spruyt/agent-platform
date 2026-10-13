# agent-platform

Temporal workers and webhook intake for homelab agents, in .NET.

One .NET 10 image runs as three roles. The role is chosen by configuration:

| Role      | Does                                                                                           | Ports                                                  |
| --------- | ---------------------------------------------------------------------------------------------- | ------------------------------------------------------ |
| `Web`     | ASP.NET minimal API (health endpoints, GitHub webhook intake) and AgentGateway, the MCP server | `8080` webhooks, `8081` MCP, set by the Web deployment |
| `Worker`  | Temporal workers on task queues `agent-platform` (workflows) and `agent-pods`                  | `8080` health only                                     |
| `Migrate` | Applies the run-data database migrations, then exits                                           | none                                                   |

The Web and Worker roles serve `/health` (all checks) and `/alive` (liveness) through `AgentPlatform.ServiceDefaults`, which also wires OpenTelemetry and HTTP resilience.

## Configuration

| Key                                       | Default                                                               | Notes                                                                                                                                                       |
| ----------------------------------------- | --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AgentPlatform:Role`                      | none, required                                                        | `Web`, `Worker` or `Migrate`; startup fails on anything else                                                                                                |
| `Temporal:Address`                        | `localhost:7233`                                                      | Both roles                                                                                                                                                  |
| `Temporal:Namespace`                      | `default`                                                             | Both roles                                                                                                                                                  |
| `AgentGateway:SessionTokenKey`            | none, required                                                        | At least 32 characters, identical on both roles; signs the per-run MCP session tokens, which expire shortly after the pod deadline                          |
| `AgentGateway:Port`                       | `8081`                                                                | Web role. The gateway port serves only `/mcp`, and `/mcp` answers only there; startup fails when it is outside 1-65535 or equals `AgentGateway:WebhookPort` |
| `AgentGateway:WebhookPort`                | `8080`                                                                | Web role. The port that serves health and webhooks; must match the Web deployment                                                                           |
| `GitHub:WebhookSecret`                    | none, required                                                        | Web role. The secret that signs GitHub webhook deliveries                                                                                                   |
| `GitHub:AppId`                            | none, required                                                        | Worker role. The id of the GitHub App                                                                                                                       |
| `GitHub:PrivateKey`                       | none, required                                                        | Worker role. The App's RSA private key in PEM format; the Web role never reads it                                                                           |
| `GitHub:ReadApp:AppId`                    | unset                                                                 | Worker role. The id of the read-only GitHub App that mints the one-repo token of a repository run. Set it together with `GitHub:ReadApp:PrivateKey`         |
| `GitHub:ReadApp:PrivateKey`               | unset                                                                 | Worker role. The read App's RSA private key in PEM format; a repository run fails without it                                                                |
| `AgentPods:MaxConcurrentActivities`       | `2`                                                                   | Worker role. The global cap on concurrent agent pods                                                                                                        |
| `AgentPods:HeartbeatInterval`             | `00:00:15`                                                            | Worker role. Must be less than half of the 60 second heartbeat timeout of the `RunAgentPod` activity                                                        |
| `AgentPods:Namespace`                     | `agent-platform-agents`                                               | Worker role                                                                                                                                                 |
| `AgentPods:Image`                         | pinned `claude-agent-read` tag and digest in `AgentPodsOptions`       | Worker role. Bump the default when the image is updated                                                                                                     |
| `AgentPods:Model`                         | `sonnet`                                                              | Worker role. Must be allowed by the tier's LiteLLM key                                                                                                      |
| `AgentPods:LiteLlmBaseUrl`                | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Injected into agent pods as `ANTHROPIC_BASE_URL`                                                                                               |
| `AgentPods:AgentGatewayUrl`               | `http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp` | Worker role. The MCP endpoint written into agent pods                                                                                                       |
| `AgentPods:ServiceAccountName`            | `agent-runner`                                                        | Worker role                                                                                                                                                 |
| `AgentPods:ClusterReadServiceAccountName` | unset                                                                 | Worker role. Required to run the `cluster-read` tier                                                                                                        |
| `AgentPods:MaxTurns`                      | `40`                                                                  | Worker role. Passed to Claude Code as `--max-turns`                                                                                                         |
| `AgentPods:MaxTokens`                     | `400000`                                                              | Worker role. Token ceiling of one run, counted from the streamed usage; a run over it is stopped and ends as `LimitExceeded`                                |
| `AgentPods:MaxSubagents`                  | `3`                                                                   | Worker role. The most `Task`/`Agent` tool calls one run may make before it is stopped as `LimitExceeded`                                                    |
| `AgentPods:UseMcpHeadersHelper`           | `false`                                                               | Worker role. Writes `headersHelper: /usr/local/bin/ap-mcp-headers` into the MCP config instead of `${VAR}` header references                                |
| `LiteLlm:BaseUrl`                         | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Where the Jev (`/typesafe`) pass-through is called; an absolute http or https URL                                                              |
| `LiteLlm:ApiKey`                          | none, required                                                        | Worker role. The LiteLLM key the Jev call authenticates with                                                                                                |
| `RenovateTriage:Mode`                     | `Shadow`                                                              | Worker role. `Shadow` records verdicts and writes nothing to GitHub; `Live` also approves, labels and comments as the App. Anything else fails startup      |
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

Pod names are derived from the workflow ID, run ID and step, so a retried activity finds its pod and re-attaches instead of creating a second one. A re-attach also reuses the pod's per-run Secret and mints no new GitHub token; the Secret is only recreated when it is missing. The runner's Role needs `create`, `get` and `delete` on Secrets. A pod that vanished while the activity waited is reported
as `PodFailed` and is not recreated.

Pod deletion has two owners. The activity deletes the pod when the workflow cancels it and when the run crosses the token or subagent limit. An attempt that ends any other way (heartbeat timeout, worker shutdown, a transient API failure) leaves the pod for the retry to re-attach to. The workflow's `DeleteAgentPod` removes the pod once the run ends or fails for good, with the pod's `activeDeadlineSeconds` and the cluster's pod cleanup as the
final backstop.

The activity timeout and the pod's `activeDeadlineSeconds` come from the same deadline (`AgentRunTimeouts`); the deadline is at most 3600 seconds. The session token of a run expires 60 seconds after the pod deadline plus the scheduling grace. The runner classifies each run from the pod's `stream-json` stdout: `Succeeded`, `Truncated` (no result event, a non-success subtype, zero output tokens or
an empty result), `UsageLimit`, `LimitExceeded`, `PodFailed` or `TimedOut`. Any outcome other than `Succeeded` ends the workflow with status `AgentFailed`.

### Repository runs on the read tier

A run with `repo`, `pullRequest` and `headSha` and a `promptName` runs a named prompt (embedded under `Prompts/`; `renovate-triage` today) against one pull request.

- The `GitHub:ReadApp` App mints an installation token for that one repository with `contents`, `pull_requests`, `checks`, `statuses`, `issues` and `actions` read. The provider's `expires_at` caps the pod deadline: `min(configured, expires_at - now - token grace - scheduling grace)`.
- The per-run Secret holds the MCP config, the prompt, the session token, the GitHub token and a git credential file. The pod gets the tokens only as environment variables (`AP_MCP_SESSION_KEY`, `AP_LITELLM_API_KEY`, `GH_TOKEN`) and reads the prompt from stdin; no token or prompt text appears in a command line.
- The `git-clone` init container clones the default branch (depth 1) into `/workspace/repo` and fetches the pull request head by sha into `/workspace/pr`. It is the only container that mounts the git credential. The `plugins` init container runs `/usr/local/bin/claude-plugins-bootstrap /workspace/repo`, which installs the plugins into the shared `claude-home` volume.
- The agent container runs in `/workspace/repo` with `--setting-sources user,project` and `--strict-mcp-config`, and mounts only the `mcp-config.json` key of the Secret. The MCP config references `${AP_MCP_SESSION_KEY}` and `${AP_LITELLM_API_KEY}`. Tool content is not exported to telemetry on the read tier.
- The session token carries the step and attempt of the run. The workflow rejects a `submit_result` whose step or attempt is not the current one.
- For `renovate-triage` the workflow validates the submitted result (verdict `SAFE`, `FIXABLE`, `BLOCKED` or `NEEDS_REVIEW`, the echoed `head_sha`, and field length caps). An invalid result comes back to the agent as a tool error, so it can resubmit.
- The runner stops a run that crosses `AgentPods:MaxTokens` or `AgentPods:MaxSubagents`; Claude Code stops itself at `AgentPods:MaxTurns`.

Start a run with the Temporal CLI in the admin-tools pod, which the frontend already accepts connections from:

```bash
kubectl -n temporal-system exec deploy/temporal-admintools -- temporal workflow start \
  --address temporal-frontend:7233 --namespace default \
  --task-queue agent-platform --type SpikeWorkflow \
  --workflow-id "spike-$(date +%s)" \
  --input '{"prompt":"Reply with a one line greeting, then call the submit_result tool.","tier":"read"}'
```

A triage run adds `"promptName":"renovate-triage","repo":"<owner>/<name>","pullRequest":<n>,"headSha":"<40 hex>"` to the input and needs `GitHub:ReadApp`.

`tier` is `read` or `cluster-read`; the `cluster-read` tier needs `AgentPods:ClusterReadServiceAccountName`. Read the outcome with `temporal workflow show --address temporal-frontend:7233 --workflow-id <id>` through the same pod, or in the Temporal UI.

## GitHub webhook intake and App client

The Web role serves `POST /webhooks/github` on the webhook port only (`/mcp` stays on the gateway port). The handler checks the `X-Hub-Signature-256` HMAC-SHA256 over the raw body with a constant-time compare before it parses anything; a missing or wrong signature gets `401` and no body is logged. Bodies over 1 MiB get `413`.

| Event                                                                                                                              | Result                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- |
| `ping`                                                                                                                             | `200`                                                    |
| `check_suite` `completed`, branch `renovate/*` except `renovate/configure`, with pull requests whose head and base are in the repo | `202`, one `CheckSuiteCompleted` signal per pull request |
| any other event or action, or a suite that fails a filter                                                                          | `200`, dropped, so GitHub never redelivers               |
| `check_suite` with a malformed body                                                                                                | `400`                                                    |

A check suite from any app is forwarded, so the workflow hears about every suite that finishes. Forwarding is a Temporal SignalWithStart: workflow type `RenovateTriageWorkflow`, ID `renovate-triage/{owner}/{repo}/{pr}`, task queue `agent-platform`, signal `CheckSuiteCompleted` with the repository, pull request number, head sha, check suite id, app slug, conclusion and `X-GitHub-Delivery` id.
`RenovateTriageWorkflow` receives these signals; see Renovate triage below.

The Worker role talks to GitHub through `IGitHubApiClient`. Reads: pull request, all check runs and suites of a sha, commits, files, reviews, labels and issue comments, all paged. Writes, used only by the `Live` triage path: an approving review pinned to a commit, dismissing a review, adding labels, and creating or editing an issue comment. Each call mints an installation token for one repository
with only the permission that call needs (`pull_requests: read` or `write`, or `checks: read`), authenticated by a short-lived App JWT signed with `GitHub:PrivateKey`. The installation is looked up per repository, and a token is reused until five minutes before it expires.

## Renovate triage

`RenovateTriageWorkflow` judges one Renovate pull request per workflow ID, for repositories owned by `anthony-spruyt` only (intake drops any other owner, and `Assess` and `Apply` refuse it). Each pass runs these activities on the `agent-platform` queue:

- `Assess` reads the pull request from GitHub and applies the rules below. It returns a skip, an earlier verdict for the head sha, a decision, `Pending` (CI still running) or a request to ask Jev. Every result carries the mode `Assess` ran in.
- `Judge` reads the pull request files and sends the pull request body and the file diff to Jev in one call (the LiteLLM `/typesafe` pass-through, each capped at 32 KiB; lock files come last in the diff, and files without a patch are listed by name) and applies the answers and the bump-type guard. It re-reads the pull request first, so a moved head returns without a verdict. It has its own retry
  options: three tries over about 15 minutes. When Jev still fails, the workflow records `needs review` with reason `jev-error`.
- `RecordVerdict` stores the verdict in `renovate_triage_verdicts`, once per pull request, head sha and mode.
- `Apply` writes the verdict to GitHub. It runs only in `Live` mode, after `RecordVerdict`.

The workflow waits for all CI: while `Assess` returns `Pending` it waits for the next `CheckSuiteCompleted` signal, for at most two hours. When the two hours end it runs `Assess` once more and records `ci-timeout` only if CI is still pending. A head sha that differs from the one judged starts a new run with the same input.

### Modes

Each `Assess` pass returns the mode it ran in, and the workflow passes that mode to `RecordVerdict` and `Apply`, so one verdict is never recorded in one mode and written in another when the configuration changes under a run. `Apply` also refuses to write when the configured mode is not `Live`.

- `Shadow` (default): the verdict is only written to the database; nothing is written to GitHub. The existing live path (n8n, acting as the triage App) can still run beside it and approves or labels the pull request while CI runs, so `Shadow` ignores its output in rules 1 to 3 (marked below); otherwise every pull request would already be skipped when CI is complete.
- `Live`: the rules below apply as written, and `Apply` writes the verdict as the triage App (see Writes).

### Rules

In order; the first that applies decides:

1. The repository is not owned by `anthony-spruyt`, the pull request is not open, or its head or base is in another repository: skipped. In `Shadow` mode a merged pull request is not skipped (Mergify may merge it before CI is complete); one closed without merging still is.
2. The author is not Renovate, or the pull request has an `agent/needs-human-review`, `agent/revert` or `blocked` label: skipped. In `Shadow` mode the `agent/needs-human-review` and `agent/auto-approved` labels are ignored.
3. An owner, member or collaborator, or the triage App, has approved the current head sha and none of them requests changes: skipped as already approved. In `Shadow` mode reviews by the triage App are ignored.
4. A verdict already exists for the head sha and mode: nothing to do. In `Live` only a verdict that `Apply` has written counts; an unapplied one is applied again on the next signal, without asking Jev again (see Writes).
5. Every commit must be authored by Renovate and verified, with GitHub as committer, as in the Mergify gate. The listed commits must match the pull request's commit count, and a count of 250 or more (the most GitHub lists) fails the gate. Otherwise: needs review, `non-renovate-commit`.
6. CI must be done. All check runs of the sha and all check suites are read; a run or suite that has not completed, or no check runs at all, is pending. Mergify checks are ignored. A failed check: needs review, `ci-failed`. The branch matching the high-risk list: needs review, `high-risk`.
7. Jev answers a breaking-change score, a bump type and a changelog score. It judges the bump type itself from the old and new versions in the diff or the body, without trusting Renovate's labels. A breaking score of 0.66 or more, or a major, calver or other type: needs review. An answer that is none of the known types: needs review, `unknown-bump`.
8. The bump-type guard (below) checks Jev's type against the diff.
9. A patch or minor on an allowlisted branch approves; any other needs a changelog score of 0.7 or more. A Jev failure: needs review, `jev-error`.

### Bump-type guard

Jev stays the judge; the code only guards the diff.

- A `digest` answer approves only when every changed line pair in the pull request diff differs in `sha256` or digest strings alone, or the new line is the old line with `@sha256:<64 hex>` appended (a Renovate pin-digest change). Anything else: needs review, `digest-mismatch`.
- For `patch` and `minor`, the old and new versions are read from the `-` and `+` line pairs of the diff (semver-like versions). If any pair disagrees with Jev's type, or a changed line pair has a different number of versions on its two sides (or a version on one side only): needs review, `bump-type-mismatch`. When the diff has no versions at all, Jev's answer stands.
- The parsed versions also fill the comment texts: a major comment shows only a semver major pair and a calver comment only a calendar-versioned pair (a year such as `2025.1` first). A comment leaves the versions out when no such pair was parsed.

### Writes

`Apply` writes with an installation token for the pull request's repository. It first re-reads the pull request and writes nothing, only logging, when the head sha is not the judged one or the pull request is not open. It has its own retry options (up to 12 tries, about 35 minutes of backoff). Errors `400`, `404`, `410` and `422` are not retried; rate limits (`403` or `429`) and `5xx` are.

- Approve: an `APPROVE` review pinned to the judged commit (skipped when the App already approved that sha) and the label `agent/auto-approved`. If a sticky comment from an earlier needs-review exists, it is edited to say the new commit was auto-approved (see the table).
- Needs review: the label `agent/needs-human-review`, the verdict comment, and a dismissal of every approval by the App, including one on the same commit. GitHub refusing a dismissal (`422`) is logged as a warning and the rest carries on.
- One sticky comment per pull request, found by the hidden marker `<!-- agent-platform:renovate-triage -->` on a comment by the App. It is created once and edited for each new head sha, never duplicated.
- When the writes are done the verdict row gets `applied_at`.
- An unapplied verdict is applied again with the App's own approval and the `agent/auto-approved` label ignored, and `agent/needs-human-review` ignored when the verdict is needs review. `blocked`, `agent/revert` and human approvals still skip it, and so does `agent/needs-human-review` when the verdict is approve.
- `Apply` itself refuses an approve verdict (`blocking-label`, nothing written, the verdict stays unapplied) while the pull request carries `agent/needs-human-review`.

The comments:

| Reason                | Text                                                                                                              |
| --------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `ci-failed`           | **Needs review:** CI failed on `<short sha>`. Failed: `<check names, linked>`.                                    |
| `high-risk`           | **Needs review:** `<matched term>` is on the high-risk list. These always get a human look.                       |
| `breaking`            | **Needs review:** likely breaking changes (Jev score `<x>`).                                                      |
| `major-bump`          | **Needs review:** major bump, `<old>` -> `<new>`.                                                                 |
| `calver-bump`         | **Needs review:** calendar-versioned bump, `<old>` -> `<new>`. The version can't show whether it breaks anything. |
| `other-bump`          | **Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways.                |
| `unknown-bump`        | **Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways.                |
| `no-changelog`        | **Needs review:** no changelog found for this bump.                                                               |
| `jev-error`           | **Needs review:** the triage check couldn't run (Jev error).                                                      |
| `bump-type-mismatch`  | **Needs review:** the versions in the diff (`<old>` -> `<new>`) don't match the bump type Jev picked (`<type>`).  |
| `digest-mismatch`     | **Needs review:** marked as a digest bump, but more than a digest changed.                                        |
| `non-renovate-commit` | **Needs review:** this PR has commits that Renovate didn't make.                                                  |
| `ci-timeout`          | **Needs review:** CI didn't finish within 2 hours.                                                                |
| `digest`              | Review body: **Auto-approved:** digest bump.                                                                      |
| `changelog-present`   | Review body: **Auto-approved:** `<patch or minor>` bump with changelog.                                           |
| `allowlisted`         | Review body: **Auto-approved:** allowlisted.                                                                      |
| Sticky edit           | **Auto-approved:** new commit `<short sha>`, `<reason>`. The reason is the review body's text, lowercased.        |
| Dismissal message     | A newer commit needs review.                                                                                      |

## Run data

Every workflow run is recorded in a PostgreSQL database (`rundata`) through EF Core, so runs can be measured, compared and replayed later.

| Table                      | One row per           | Key columns                                                                                                                                                                                                          |
| -------------------------- | --------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `runs`                     | workflow run          | `workflow_id` + `workflow_run_id` (unique), `workflow_type`, `status`, `input_event` and `context` (jsonb), `prompt_hash`, `model`, timings, token and cost totals, nullable `outcome` fields for later ground truth |
| `run_steps`                | step of a run         | `run_id` + `seq` (unique), `name`, `rung` (`Rule`, `Jev`, `Claude`), `input` and `output` (jsonb), `confidence`, `stop_reason`, `prompt_hash`, `model`, tokens, `cost_usd`, timings                                  |
| `renovate_triage_verdicts` | pull request head sha | `repo` + `pr_number` + `head_sha` + `mode` (unique), `verdict` (`Approve` or `NeedsReview`), `reasons` (jsonb), `comment`, `created_at`, `applied_at` (set once a `Live` verdict is on GitHub)                       |
| `prompts`                  | distinct prompt       | `hash` (SHA-256 of the body, primary key), `name`, `body`                                                                                                                                                            |

Only activities write to the database, so workflow code stays deterministic. `BeginRun`, `RecordStep` and `CompleteRun` run on the `agent-platform` queue and are idempotent on the unique keys above, so a retried activity never duplicates a row. A failed or cancelled run is completed as `Failed`.

NUL characters are stripped from text and jsonb values before they are written, because PostgreSQL rejects them.

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

The AppHost starts a Temporal dev server (`temporalio/temporal`, CLI `1.9.x`, which embeds Temporal server 1.32 like the cluster) in the `agent-platform` namespace, a PostgreSQL container with the `rundata` database, the `Migrate` role (the Worker waits for it to finish) and the Host as Web and Worker, and opens the Aspire dashboard at `http://localhost:15170`. The dev server UI is at
`http://localhost:8233`. With Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`.

## Build and test

```bash
dotnet build
dotnet test --project tests/AgentPlatform.Tests
dotnet test --project tests/AgentPlatform.IntegrationTests
dotnet format AgentPlatform.slnx --verify-no-changes
```

The workflow tests run against Temporal's time-skipping test server, which the SDK downloads on first use.

The replay tests (`tests/AgentPlatform.Tests/Replay`) replay the committed histories in `Replay/Histories` against the current workflow code and fail on non-determinism. After an intentional workflow change, re-record them with `RECORD_HISTORIES=1 dotnet test --project tests/AgentPlatform.Tests` and commit the diff; normal runs never rewrite them. The histories are synthetic, recorded by the
time-skipping tests. The `renovate-triage-v141-*` histories were captured from release 1.4.1 while a run waited for CI or for Jev; no test records them, so they stay as committed and prove that runs started by 1.4.1 replay on the current code. Every workflow change must sit behind `Workflow.Patched` (`renovate-triage-jev-retry-v1`, `renovate-triage-live-v1`).

The integration tests share one AppHost (Temporal dev server, PostgreSQL, Migrate and both Host roles) through `Aspire.Hosting.Testing`. They run `PingWorkflow` end to end, check its `runs` row, check that the Migrate role finished with exit code 0 and post a signed `check_suite` delivery to the Web role to see the triage workflow schedule its `Assess` activity on the Worker. They need a container
runtime, which GitHub's ubuntu runners have; with Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`.

## Image

```bash
docker build -t agent-platform .
docker run --rm -e AgentPlatform__Role=Web agent-platform
```

The runtime image is the chiseled Ubuntu ASP.NET image running as UID `1654`. It needs no writable root filesystem, so it runs under Pod Security `restricted` with `readOnlyRootFilesystem`. Releases are cut by release-please and published to GHCR by the shared CI.
