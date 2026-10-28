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

| Key                                        | Default                                                               | Notes                                                                                                                                                       |
| ------------------------------------------ | --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `AgentPlatform:Role`                       | none, required                                                        | `Web`, `Worker` or `Migrate`; startup fails on anything else                                                                                                |
| `Temporal:Address`                         | `localhost:7233`                                                      | Both roles                                                                                                                                                  |
| `Temporal:Namespace`                       | `default`                                                             | Both roles                                                                                                                                                  |
| `AgentGateway:SessionTokenKey`             | none, required                                                        | At least 32 characters, identical on both roles; signs the per-run MCP session tokens, which expire shortly after the pod deadline                          |
| `AgentGateway:Port`                        | `8081`                                                                | Web role. The gateway port serves only `/mcp`, and `/mcp` answers only there; startup fails when it is outside 1-65535 or equals `AgentGateway:WebhookPort` |
| `AgentGateway:WebhookPort`                 | `8080`                                                                | Web role. The port that serves health and webhooks; must match the Web deployment                                                                           |
| `GitHub:WebhookSecret`                     | none, required                                                        | Web role. The secret that signs GitHub webhook deliveries                                                                                                   |
| `GitHub:AppId`                             | none, required                                                        | Worker and Web roles. The numeric id of the GitHub App; startup fails when it is missing or not a number. The webhook drops check suites of this App        |
| `GitHub:PrivateKey`                        | none, required                                                        | Worker role. The App's RSA private key in PEM format; the Web role never reads it                                                                           |
| `GitHub:ReadApp:AppId`                     | unset                                                                 | Worker role. The id of the read-only GitHub App that mints the one-repo token of a repository run. Set it together with `GitHub:ReadApp:PrivateKey`         |
| `GitHub:ReadApp:PrivateKey`                | unset                                                                 | Worker role. The read App's RSA private key in PEM format; a repository run fails without it                                                                |
| `AgentPods:MaxConcurrentActivities`        | `2`                                                                   | Worker role. The global cap on concurrent agent pods                                                                                                        |
| `AgentPods:HeartbeatInterval`              | `00:00:15`                                                            | Worker role. Must be less than half of the 60 second heartbeat timeout of the `RunAgentPod` activity                                                        |
| `AgentPods:Namespace`                      | `agent-platform-agents`                                               | Worker role                                                                                                                                                 |
| `AgentPods:Image`                          | pinned `claude-agent-read` tag and digest in `AgentPodsOptions`       | Worker role. Bump the default when the image is updated                                                                                                     |
| `AgentPods:Model`                          | `sonnet`                                                              | Worker role. Must be allowed by the tier's LiteLLM key                                                                                                      |
| `AgentPods:LiteLlmBaseUrl`                 | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Injected into agent pods as `ANTHROPIC_BASE_URL`                                                                                               |
| `AgentPods:AgentGatewayUrl`                | `http://agent-platform-web.agent-platform.svc.cluster.local:8081/mcp` | Worker role. The MCP endpoint written into agent pods                                                                                                       |
| `AgentPods:ServiceAccountName`             | `agent-runner`                                                        | Worker role                                                                                                                                                 |
| `AgentPods:ClusterReadServiceAccountName`  | unset                                                                 | Worker role. Required to run the `cluster-read` tier                                                                                                        |
| `AgentPods:MaxTurns`                       | `40`                                                                  | Worker role. Passed to Claude Code as `--max-turns`                                                                                                         |
| `AgentPods:MaxBudgetUsd`                   | `2`                                                                   | Worker role. Passed to Claude Code as `--max-budget-usd` (dollars, per run)                                                                                 |
| `AgentPods:UseMcpHeadersHelper`            | `false`                                                               | Worker role. Writes `headersHelper: /usr/local/bin/ap-mcp-headers` into the MCP config instead of `${VAR}` header references                                |
| `AgentPrompts:Directory`                   | unset                                                                 | Worker role. Directory of `<prompt-name>.md` files (e.g. `renovate-triage.md`) that override the built-in prompts, which stay the fallback                  |
| `LiteLlm:BaseUrl`                          | `http://litellm.litellm.svc.cluster.local:4000`                       | Worker role. Where LiteLLM serves Jev; an absolute http or https URL. A path in it needs a trailing slash, or the Jev path replaces its last segment        |
| `LiteLlm:ApiKey`                           | none, required                                                        | Worker role. The LiteLLM key the Jev call authenticates with                                                                                                |
| `LiteLlm:JevPath`                          | `v1/systemone`                                                        | Worker role. The Jev route on LiteLLM, relative to `LiteLlm:BaseUrl`; no scheme, host, query, fragment, leading slash or dot segment, or startup fails      |
| `LiteLlm:JevModel`                         | `jev-1.14.0`                                                          | Worker role. The Jev model sent with every call; pinned so a moved alias cannot change triage. Non-empty, printable, no whitespace, or startup fails        |
| `RenovateTriage:Mode`                      | `Shadow`                                                              | Worker role. `Shadow` records verdicts and writes nothing to GitHub; `Live` also approves, labels and comments as the App. Anything else fails startup      |
| `RenovateTriage:Migration:PathGlobs`       | `talos/**`, `**/topf.yaml`                                            | Worker role. Changed paths that mark a pull request `hands-on-migration`; a configured list replaces the default                                            |
| `RenovateTriage:Migration:DependencyGlobs` | `siderolabs/*`, `kubelet`, `kube-apiserver`, `kubernetes/*`           | Worker role. Dependency names that mark a pull request `hands-on-migration`; a configured list replaces the default                                         |
| `RenovateTriage:Claude:MaxPerHour`         | `2`                                                                   | Worker role. Cap on pod-creating Claude runs in a rolling hour; open reservations count up to 2 hours. `security` PRs skip it. At least 1, or startup fails |
| `RenovateTriage:Claude:MaxPerDay`          | `8`                                                                   | Worker role. The same cap over a rolling 24 hours; `security` PRs skip it too. At least 1                                                                   |
| `RenovateTriage:Claude:MaxPerPrPerDay`     | `2`                                                                   | Worker role. The same cap per pull request over a rolling 24 hours. At least 1                                                                              |
| `RenovateTriage:Sweeper:Interval`          | `00:30:00`                                                            | Worker role. How often the sweeper runs (a .NET time span). At least one minute, or startup fails; a new value updates the schedule at start                |
| `TextScanner:BaseUrl`                      | `http://litellm-llm-tool-guard.litellm.svc:8080`                      | Worker role. The llm-tool-guard service that scans untrusted pull request text; an absolute http or https URL                                               |
| `TextScanner:RequestTimeout`               | `00:05:30`                                                            | Worker role. Time allowed for one scan request; a slower one leaves the text unchecked                                                                      |
| `TextScanner:RetryBudget`                  | `00:10:00`                                                            | Worker role. How long a busy (503) or unreachable scanner is retried before the text is left unchecked                                                      |
| `TextScanner:RetryDelay`                   | `00:00:05`                                                            | Worker role. Pause between scanner retries                                                                                                                  |
| `ConnectionStrings:rundata`                | none, required                                                        | Worker and Migrate roles. The Npgsql connection string of the run-data database                                                                             |
| `OTEL_EXPORTER_OTLP_ENDPOINT`              | unset                                                                 | Enables the OTLP exporter when set                                                                                                                          |
| `ASPNETCORE_HTTP_PORTS`                    | `8080`                                                                | Image default; the Web deployment sets `8080;8081`                                                                                                          |

Environment variables use `__` as the separator, for example `AgentPlatform__Role=Worker`.

In the cluster the run-data connection string uses `SSL Mode=VerifyFull` with `Root Certificate=/etc/agent-platform/rundata-ca/ca.crt`, the mounted CA of the database cluster.

## Agent pods

The Claude rung of `RenovateTriageWorkflow` (see Renovate triage) runs one agent pod per attempt.

1. The workflow runs the `RunAgentPod` activity on task queue `agent-pods`.
2. The activity (`KubernetesAgentRunner`) creates a Claude Code pod in `AgentPods:Namespace` and then a per-run Secret owned by the pod. It watches the pod to completion, heartbeating. The workflow then runs the `DeleteAgentPod` activity as a local activity, also when the run activity fails, so cleanup never waits behind long runs on the `agent-pods` queue.
3. The agent calls the `submit_result` tool of AgentGateway. The gateway checks the per-run session token and sends the result to the waiting workflow as a Temporal Update. The workflow accepts the first result and rejects any further one, so a repeated `submit_result` call gets an error.
4. The workflow parses the submitted result into a verdict, or ends the attempt as failed.

Pod names are derived from the workflow ID, run ID and step, so a retried activity finds its pod and re-attaches instead of creating a second one. A re-attach keeps the pod's per-run Secret and its deadline. If the pod has started (Running, ended, or an init container running or finished), the kubelet already mounted the Secret, so the runner mints nothing and creates nothing.
If it is still Pending, the Secret may be missing (the worker died before creating it): the runner asks for the run's credentials again, creates the Secret and leaves an existing one untouched on `AlreadyExists`.
The runner's Role needs only `create` and `delete` on Secrets. A pod that vanished while the activity waited is reported as `PodFailed` and is not recreated.

Pod deletion has two owners. The activity deletes the pod when the workflow cancels it. An attempt that ends any other way (heartbeat timeout, worker shutdown, a transient API failure) leaves the pod for the retry to re-attach to. The workflow's `DeleteAgentPod` removes the pod once the run ends or fails for good, with the pod's `activeDeadlineSeconds` and the cluster's pod cleanup as the
final backstop.

The activity timeout and the pod's `activeDeadlineSeconds` come from the same deadline (`AgentRunTimeouts`); the deadline is at most 3600 seconds. The session token of a run expires 60 seconds after the pod deadline plus the scheduling grace. The runner classifies each run from the pod's `stream-json` stdout: `Succeeded`, `Truncated` (no result event, a non-success subtype, zero output tokens or
an empty result), `UsageLimit`, `LimitExceeded` (Claude Code stopped at `--max-turns` or `--max-budget-usd`), `PodFailed` or `TimedOut`. Any outcome other than `Succeeded` ends the attempt as failed.

### Repository runs on the read tier

The Claude rung runs the `renovate-triage` prompt (embedded under `Prompts/`, where `AgentPrompts:Directory` can override it) against one pull request head.

- The `GitHub:ReadApp` App mints a read-only installation token for that one repository. The provider's `expires_at` caps the pod deadline: `min(configured, expires_at - now - token grace - scheduling grace)`.
- The per-run Secret holds the MCP config, the prompt, the session token, the GitHub token and a git credential file. The pod gets the tokens only as environment variables (`AP_MCP_SESSION_KEY`, `AP_LITELLM_API_KEY`, `GH_TOKEN`) and reads the prompt from stdin; no token or prompt text appears in a command line.
- The `git-clone` init container clones the default branch (depth 1) into `/workspace/repo` and fetches the pull request head by sha into `/workspace/pr`. It is the only container that mounts the git credential. The `plugins` init container runs `/usr/local/bin/claude-plugins-bootstrap /workspace/repo`, which installs the plugins into the shared `claude-home` volume.
- The agent container runs in `/workspace/repo` with `--setting-sources user,project` and `--strict-mcp-config`, and mounts only the `mcp-config.json` key of the Secret. The MCP config references `${AP_MCP_SESSION_KEY}` and `${AP_LITELLM_API_KEY}`. Tool content is not exported to telemetry on the read tier.
- The `get_triage_context` tool of AgentGateway returns one numbered section of the context the worker pre-fetched for the run (facts and an index first, then changelogs, CI logs and other fetched text), at most 50,000 characters each. Text from outside the system sits inside blocks with a random per-context tag that the first section names, so it is read as data.
  The tool needs the same per-run session token and answers only while the run is active.
- The session token carries the step and attempt of the run. The workflow rejects a `submit_result` whose step or attempt is not the current one.
- For `renovate-triage` the workflow validates the submitted result (verdict `SAFE`, `FIXABLE`, `BLOCKED` or `NEEDS_REVIEW`, the echoed `head_sha`, and field length caps). An invalid result comes back to the agent as a tool error, so it can resubmit once; a second invalid result ends the attempt as failed, and the workflow keeps the parsed result, not the raw payload.
- The runner reads pod logs line by line with a 1 MiB cap per line (longer lines are skipped and counted) and caps the final read in bytes. A failed or unpullable init container ends the run as `PodFailed`, naming the container. Runs stop at the configured turn (`AgentPods:MaxTurns`) and budget (`AgentPods:MaxBudgetUsd`) limits and end as `LimitExceeded`.

## GitHub webhook intake and App client

The Web role serves `POST /webhooks/github` on the webhook port only (`/mcp` stays on the gateway port). The handler checks the `X-Hub-Signature-256` HMAC-SHA256 over the raw body with a constant-time compare before it parses anything; a missing or wrong signature gets `401` and no body is logged. Bodies over 1 MiB get `413`. A body that arrives too slowly for Kestrel (`BadHttpRequestException`) gets `408` and one Warning with the delivery id and the event name (`unknown` unless the `X-GitHub-Event` header is lowercase letters and underscores, up to 40), so the delivery can be traced and redelivered.

| Event                                                                                                                              | Result                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- |
| `ping`                                                                                                                             | `200`                                                    |
| `check_suite` `completed`, branch `renovate/*` except `renovate/configure`, with pull requests whose head and base are in the repo | `202`, one `CheckSuiteCompleted` signal per pull request |
| any other event or action, or a suite that fails a filter                                                                          | `200`, dropped, so GitHub never redelivers               |
| `check_suite` with a malformed body                                                                                                | `400`                                                    |

A check suite from any app except this one (`GitHub:AppId`) is forwarded, so the workflow hears about every suite that finishes.
Forwarding is a Temporal SignalWithStart: workflow type `RenovateTriageWorkflow`, ID `renovate-triage/{owner}/{repo}/{pr}`, task queue `agent-platform`, signal `CheckSuiteCompleted` with the repository, pull request number, head sha, check suite id, app slug, conclusion and `X-GitHub-Delivery` id.
`RenovateTriageWorkflow` receives these signals; see Renovate triage below.

The Worker role talks to GitHub through `IGitHubApiClient`. Reads: pull request, all check runs, commit statuses and check suites of a sha, commits (with their parents), files, comparisons between two refs, reviews, labels, issue comments, releases and file contents of a changelog source repository, and job logs, all paged where listed.
Writes: an approving review pinned to a commit, dismissing a review, adding and removing labels, creating or editing an issue comment (the `Live` triage path), creating or updating check runs, and rerunning failed jobs. Each call mints an installation token for one repository
with only the permission that call needs (`pull_requests: read` or `write`, `checks: read` or `write`, `statuses: read` for the commit statuses of a sha, `contents: read` for changelogs and release notes of a source repository and for comparing refs, or `actions: read` or `write` to read workflow runs and rerun failed jobs), authenticated by a short-lived App JWT signed with `GitHub:PrivateKey`.
The App installation must therefore grant the repository permissions Pull requests, Checks, Commit statuses, Contents and Actions.
Removing a label uses the same Pull requests write permission as adding one.
The sweeper also calls the App endpoints that take the App JWT itself and no installation token: listing and redelivering webhook deliveries, and finding the installation. Listing the installation's repositories uses a Metadata read token.
The installation is looked up per repository, and a token is reused until five minutes before it expires.

`GitHub:AppId` is required and numeric on both roles. The check-run list leaves out the App's own check runs whose `external_id` starts with `renovate-triage/`, the check-suite list leaves out the App's own suites, and the webhook drops its own check suites, so the App's triage runs and suites never count as CI.
Check runs are written through `ICheckRunReporter`: it finds the newest run with that name and `external_id` on the commit first, creates it when missing, updates it while it is open and leaves a completed run as it is. GitHub never reopens a completed run, so a report with `StartNew` set (queued or in progress) creates a new run of the same name when the newest one is completed, and reuses an open one; the PR page shows the newest run per name. Any workflow that reports checks can use it.

## Untrusted text scanner

`IUntrustedTextScanner` sends one text per request to the llm-tool-guard `POST /v1/scan`, one request at a time across the worker, and returns the text with its hash (`sha256:` and the lowercase hex of the UTF-8 bytes) as a `ScannedText`. Consumers use only that text and refuse one whose hash no longer matches (`ScannedText.IsIntact`).
The outcome is `Clean`, `Flagged` or `Unchecked`: a busy (503) or unreachable scanner is retried until `TextScanner:RetryBudget` runs out, and a timeout, any other status or an unreadable answer is `Unchecked`.

## Renovate triage

`RenovateTriageWorkflow` judges one Renovate pull request per workflow ID, for repositories owned by `anthony-spruyt` only (intake drops any other owner, and `Assess` and `Apply` refuse it). Each pass runs these activities on the `agent-platform` queue:

- `Assess` reads the pull request from GitHub and applies the rules below. It returns a skip, an earlier verdict for the head sha, a decision, `Pending` (CI still running) or a request to ask Jev. Every result carries the mode `Assess` ran in.
- `Judge` reads the pull request files and sends the pull request body and the file diff to Jev in one call (`LiteLlm:JevPath`, by default LiteLLM's native `v1/systemone`, each capped at 32 KiB; lock files come last in the diff, and files without a patch are listed by name) and applies the answers and the bump-type guard. It re-reads the pull request first, so a moved head returns without a verdict. It has its own retry
  options: three tries over about 15 minutes. When Jev still fails, the workflow records `needs review` with reason `jev-error`.
- `RecordVerdict` stores the verdict in `renovate_triage_verdicts`, once per pull request, head sha and mode.
- `Apply` writes the verdict to GitHub. It runs only in `Live` mode, after `RecordVerdict`.
- The code rungs (`Precheck`, `FetchChangelog`, `DetectMigration`, `ClassifyCi`, `RerunFailedJobs` and `BuildContext`, on `RenovateTriageCodeActivities`) and the check-run activity `RenovateTriageReportCheck` run on the same queue; see Code rungs below.

The workflow waits for all CI: while `Assess` returns `Pending` it waits for the next `CheckSuiteCompleted` signal, for at most two hours, and wakes every five minutes to run `Assess` again, so a lost `check_suite` webhook delays the verdict by five minutes at most. A wait that began before the `renovate-triage-ci-poll-v1` patch was deployed keeps the older rule: it wakes every five minutes only while a commit status is among the pending items (nothing signals a status completing) and the `renovate-triage-status-poll-v1` patch applies, and otherwise ends on the signal alone. When the two hours end it runs `Assess` once more and records `ci-timeout` only if CI is still pending. A head sha that differs from the one judged starts a new run with the same input.

### Code rungs

A run started after the `renovate-triage-step2-v1` patch also does the following; a run started before it carries on as before. None of the rungs below calls Claude; the Claude rung follows them.

1. Once `Assess` has passed the commit gate it returns `NeedsPrecheck`. `Precheck` reads the pull request head, files and commits, checks that the diff is only version and digest updates (the file count must match the pull request, and the head must not move while it reads), and sends the pull request body through `IUntrustedTextScanner`.
   A diff beyond version updates, or a body that comes back `Flagged` or `Unchecked`, ends the run as needs review with reason `precheck-diff`, `precheck-flagged` or `precheck-unchecked`: Jev is never called, and the step is recorded as `precheck`.
   An approve is prechecked again before `Apply` when its head was not prechecked, and that step is recorded as `apply-precheck`.
   When it fails, the flag is written but a stored approval for that head stays unapplied, and the `agent/needs-human-review` label then keeps `Assess` from approving it.
2. `DetectMigration` reads the scanned body and the changed files; a hands-on migration (`RenovateTriage:Migration:*` globs) is needs review with reason `hands-on-migration`.
3. When CI has failed, `ClassifyCi` sends the tails of the failing job logs (at most 30,000 characters each; each tail ends at the log's last `##[error]` line, or is the plain tail when there is none) to Jev as read from GitHub, not through the scanner. Jev's answer is one of three fixed values: `Flaky`, `Real` or `Unsure`.
   A Jev error is `Unsure`, and the recorded reason is `jev_error`. In `Live` mode a `Flaky` answer reruns the failed jobs of each run once (`RerunFailedJobs`; a run on its second attempt is never rerun, and finding nothing failed counts as a rerun),
   then waits for the next check suite to complete and judges again. `Shadow` mode classifies but never reruns.
   Anything else is needs review with reason `ci-failed`. The CI wait itself is `CiWait`: the two hours start at the first pending pass and start over after a rerun, and the wait after a rerun uses the same two hours.
4. When neither the code nor Jev finds a changelog, `FetchChangelog` fetches the release notes of each update in the body and scans each section. Jev is asked once more only when every section is `Clean`, and the code checks the fetched sections for release notes; if any section is held back, the pull request stays needs review with reason `no-changelog`.
5. In `Live` mode only, once `Apply` has written a needs-review verdict for a reason the code cannot settle by itself (not `non-renovate-commit`, a precheck reason or `hands-on-migration`),
   `BuildContext` builds the triage context for the Claude rung: typed facts (repository, pull request, head sha, reason, CI state, count of failing jobs, texts held back) plus the scanned pull request body, changelog sections and CI log tails (each ending at the log's last `##[error]` line, or the plain tail when there is none),
   each in a block with a random per-context tag. A CI job is titled `CI job <n>` in the trusted index; its name sits inside the scanned block.
   The CI state is one fact line from the same rules `Apply` uses, over the check runs, commit statuses and check suites with Mergify, `renovate/stability-days` and the `Agent triage` check left out: `CI: green`, or `CI: pending` or `CI: failing` with a count of checks when there are any and no names.
   The changelogs are fetched only when the body scan is clean.
   The workflow keeps it for the `get_triage_context` query that the Claude rung's pod reads.
   Once the `renovate-triage-claude-v1` patch applies, a context that cannot be built parks the run (see Claude rung); before it, the run records the failed step and carries on.

The pull request body, the changelog sections and the log tails in the triage context travel as `ScannedText`, and the activities refuse one whose hash does not match.

The workflow reports a check run named `Agent triage` on the head sha, only in `Live` mode: in progress while it works, then one of `success` (verdict applied), `neutral` (verdict not applied or only recorded, head moved on, or cancelled), `skipped` or `failure` (the run stopped on an error).
The check run is written in a `finally`, so it never stays open, and a failure to write it is logged without stopping the triage. Its `external_id` is `renovate-triage/{repo}/{pr}@{sha}`. For a run started after the `renovate-triage-fresh-check-v1` patch, the first report a run makes for a head, once it is past the skip and already-judged gates, sets `StartNew`, so a re-triage of a head whose check was completed (for example `Skipped: blocking-label`, after the owner removed the label) shows a new run going in progress and then its result. A skipped, already-judged or duplicate run leaves a completed check as it is. The sweeper works on the newest run.

### Claude rung

A run started after the `renovate-triage-claude-v1` patch climbs to Claude in `Live` mode when a pull request would be handed to the owner for a reason the code cannot settle by itself.
These reasons never climb: `non-renovate-commit`, the precheck reasons and `hands-on-migration`. Each of them is handed to the owner as before. `ci-failed`, `ci-timeout` and `breaking` do climb.
A verdict already stored for the head is never climbed again. `Shadow` mode never climbs.

1. `QueueAsync` writes a pending row in `renovate_triage_pending`, adds the `agent/needs-triage` label and a sticky "Queued" comment, and no verdict row. A head that moved meanwhile reassesses.
   A mode that is no longer `Live` writes nothing and ends the run skipped with reason `not-live`.
2. `BuildContext` builds the triage context. A context that cannot be built parks the run: the activity failed, the head is not the last commit of the pull request, or the commit message table cannot be read.
3. `ReserveAsync` reserves a Claude run against the caps in `RenovateTriage:Claude:*`, and returns one of three answers. Reserved: a pod may run. Park: a cap or a cooldown is in force, and the run parks until it ends. Owner: the head already has 2 failed attempts, or the repository breaker is open (3 consecutive failed runs, open for an hour), and the pull request goes to the owner as needs review.
   A pull request that carries the `security` label (read by `QueueAsync`) skips the hourly and daily caps. Its runs still count toward them for every other pull request, and the cooldowns, the per-pull-request cap, the failed-attempt limit and the breaker apply to it as to any other.
4. The pod runs as described in Agent pods. While it runs the workflow reads the head again after each check suite signal. A head that moved cancels the pod, finishes the reservation as cancelled and continues as new on the same input.
5. The parsed result is stored as the verdict with rung `Claude`; `SAFE` goes through the same CI and status gates in `Apply` as any other `SAFE`. A pod failure or an invalid result counts as a failed attempt and the loop reserves again. A usage-limit stop counts as a failed attempt.
   It sets the global cooldown and parks until the end the cooldown now has (15 minutes to 24 hours from now; one hour when the pod gave no reset time).

Parking ends the run with the check run `Queued`. Its title is the cause and the next try time (the date is left out when it is today in UTC), so the PR list shows them without opening the check. The pending row and the `agent/needs-triage` label stay, and the next `CheckSuiteCompleted` signal for the pull request, or the sweeper (see Sweeper), starts the run again. `Apply` removes the label with the verdict. Queuing and applying a verdict also remove the other verdict labels (`agent/safe`, `agent/auto-approved`, `agent/fixable`) that an older head left, through the shared `SetExclusiveLabelAsync` helper, which removes the present labels of a mutually exclusive group except the one to set.
It also settles an open pending row left for an older head of the same pull request. Every pull request comment of the rung is built from the fixed sticky-comment templates; agent text sits in the quoted, defused block described under Writes.

The worker exports three gauges on the meter `AgentPlatform.Triage`, refreshed every 30 seconds: `triage_pending_total` (open pending rows; held (`blocked`) ones are not counted), `claude_cooldown_active` (cooldowns in force) and `sweeper_last_success_timestamp` (Unix seconds of the last sweep in which every step succeeded; `0` before the first).

### Sweeper

A hosted service on the worker creates the Temporal Schedule `renovate-triage-sweeper` at startup, or updates it when it exists, so a changed `RenovateTriage:Sweeper:Interval` takes effect on the next start. The schedule starts `RenovateTriageSweeperWorkflow` on the `agent-platform` queue and skips a run while the previous one is still going. A failed create or update is retried every 30 seconds. The schedule is replaced as a whole on every start, so a pause set by hand does not survive a restart.
Each sweep runs three steps in order and records each as a `Rule` step (`redrive`, `redeliver`, `reconcile`) in the run data. A step that fails, or that could not reach GitHub for some of its rows (`errors` above zero in its output), is recorded with stop reason `error` after it has finished the other rows, and the other steps still run; only a sweep in which all three succeed stamps `sweeper_state.last_success_at`.

1. `redrive` (`Live` mode only) takes the open rows of `renovate_triage_pending` (`Pending` and `Redriven`), oldest first, and leaves alone any whose triage workflow is still running. Rows of another owner are ignored.
   A closed or merged pull request, one labelled `agent/needs-human-review`, and one the owner (`anthony-spruyt`) reviewed, or relabelled on the triage labels, since the row was opened (created, or its head last moved) are settled instead of re-driven, and their open `Agent triage` check completes as `neutral`.
   A pull request that carries `blocked` or `agent/revert`, or that is already approved while no verdict for its head waits to be applied, would end the re-driven run as skipped, so the row stays as it is and takes no slot. Each sweep also sets `held_at` on such a row whose pull request carries `blocked`, and clears it once the label is gone.
   The rest go through the Claude budget: nothing is re-driven during the global cooldown. Pull requests that carry the `security` label go first, oldest first, and are re-driven even when no hourly or daily slot is free; each uses one slot, and the others, oldest first, fill only the slots that remain. A repository with a cooldown or an open breaker and a pull request at its daily cap are skipped, security ones included. Each pick gets the same SignalWithStart as a webhook and the row becomes `Redriven`.
2. `redeliver` pages the App's webhook deliveries newest first (`GET /app/hook/deliveries`, authenticated as the App) until a page reaches back past the window start, up to 50 pages. The window starts 10 minutes before the newest delivery of the previous sweep, and never more than 3 days back.
   Deliveries are grouped by `guid`, and a guid is judged by its newest attempt, which must lie inside the window. A `check_suite` guid with no `2xx` attempt is redelivered through that newest attempt, at most 3 times; a `422` answer counts as done. The newest `delivered_at` seen is stored in `sweeper_state.last_seen_delivered_at`.
3. `reconcile` lists, in every repository of the installation that has pull requests turned on, the open pull requests that Renovate opened and that were updated in the last 3 days. One that has no `Agent triage` check for its head, no open pending row, no running triage, no blocking label and no verdict is signalled. The sweeper also completes as `failure` any `Agent triage` check that is still open although the row is gone and no workflow is running.

### Modes

Each `Assess` pass returns the mode it ran in, and the workflow passes that mode to `RecordVerdict` and `Apply`, so one verdict is never recorded in one mode and written in another when the configuration changes under a run. `Apply` also refuses to write when the configured mode is not `Live`.

- `Shadow` (default): the verdict is only written to the database; nothing is written to GitHub, no failed job is rerun and no context is built.
  The existing live path (n8n, acting as the triage App) can still run beside it and approves or labels the pull request while CI runs, so `Shadow` ignores its output in rules 1 to 3 (marked below); otherwise every pull request would already be skipped when CI is complete.
- `Live`: the rules below apply as written, and `Apply` writes the verdict as the triage App (see Writes).

### Rules

In order; the first that applies decides:

1. The repository is not owned by `anthony-spruyt`, the pull request is not open, or its head or base is in another repository: skipped. In `Shadow` mode a merged pull request is not skipped (Mergify may merge it before CI is complete); one closed without merging still is.
2. The author is not Renovate, or the pull request has an `agent/needs-human-review`, `agent/needs-triage`, `agent/revert` or `blocked` label: skipped. The `agent/needs-triage` label parks a pull request for the agent, so a hand-added one never starts a run.
  In `Shadow` mode, and in `Live` while the head has an unapplied verdict, the `agent/needs-human-review`, `agent/needs-triage` and `agent/auto-approved` labels are ignored; an unapplied approving verdict still honours `agent/needs-human-review`.
3. An owner, member or collaborator, or the triage App, has approved the current head sha and none of them requests changes: skipped as already approved. In `Shadow` mode reviews by the triage App are ignored.
4. A verdict already exists for the head sha and mode: nothing to do. In `Live` only a verdict that `Apply` has written counts; an unapplied one is applied again on the next signal, without asking Jev again (see Writes).
5. Every commit must be authored by Renovate and verified, with GitHub as committer, or be a commit that only merges the base branch in (see Merge-from-base commits). The listed commits must match the pull request's commit count, and a count of 250 or more (the most GitHub lists) fails the gate. Otherwise: needs review, `non-renovate-commit`. The Mergify gate is separate: its `gate-files` rule requires the owner's approval, or Renovate-only authors, for a pull request that touches gate files.
6. CI must be done. All check runs and commit statuses of the sha and all check suites are read; a run, status or suite that has not completed, or no check runs or statuses at all, is pending. Mergify checks are ignored. A failed check: needs review, `ci-failed`. The branch matching the high-risk list: needs review, `high-risk`.
7. The code checks only the scanned body and any fetched changelog sections for release notes.
   A body has them when every update in its table has a closed `<details>` block under `### Release Notes`, matched by the block's summary to the update's source repository (or to its dependency name when it has no source; updates of one source repository and new version share a block).
   A block cut off by Renovate's truncation notice or by the `### Configuration` footer counts up to that point.
   The block's first Renovate version section (a `` ### [`version`](url) `` heading) must cover the update's new version:
   - A plain version needs the same version as the heading.
   - A short tag (`24`, `1.2`) needs a heading of digits and dots that starts with it and a dot (`24.15.0`).
   - A suffixed variant tag (`1.2.3-alpine`, `1.2.3-bookworm`) needs a heading equal to its version part (`1.2.3`).
   - A prerelease tag is no variant, so it needs the same version as the heading. A suffix with `rc`, `alpha`, `beta`, `pre`, `dev`, `canary`, `next`, `snapshot` or `nightly` after a dash, or a milestone (`M1`) or `ea` token after a dash or dot, is one (`-rc.1`, `-beta2`, `-preview.3`, `-alpine-rc1`, `-SNAPSHOT`, `-M1`, `21-ea`).
   - A range (starting with `^`, `~`, `<`, `>` or `*`, or holding a space, comma or pipe) needs a heading equal to the resolved version its table row shows (the Renovate diff link or the age or adoption badge, which must agree once a leading `v` is ignored). Without one, it is compared number by number with the lower bound of a single operator followed by a version: `^`, `~`, `~>` and `>=` need a heading at or above it, `>` a heading above it and `=` a heading equal to it. Any other range counts as none.
   That section must have a list item (`-`, `*`, `+`, `•`, `1.` or `1)`) that is more than a link or URL.
   A dependency-bump line (`Updated dependencies`), a release-bot line (`Release name-v1.2.3`, `sync(chart): v1.2.3`), a `Full Changelog` line, a first-contribution note and the lists nested under them don't count, and neither do items inside a code block, indented or fenced.
   The fetched changelog sections have them when every update in the table can be fetched, there are at most 5 fetches (updates of one source repository and new version share one), and each has a section with such a list item.
   An update can be fetched when its source link is a GitHub repository root; a link into a directory of a repository (a monorepo package) can't be fetched.
   If any update can't be fetched or there are more than 5, the sections never count. A table with 50 rows, the most the parser reads, counts as none, because rows past it are never checked.
   Anything else, such as prose only, a title line only or link-only items, counts as none.
   Jev answers a breaking-change score, a bump type and a changelog score. It judges the bump type itself from the old and new versions in the diff or the body, without trusting Renovate's labels. A breaking score of 0.66 or more, or a major, calver or other type: needs review. An answer that is none of the known types: needs review, `unknown-bump`.
8. The bump-type guard (below) checks Jev's type against the diff.
9. A patch or minor on an allowlisted branch approves; any other needs release notes found in code, or else a changelog score of 0.7 or more from Jev, so Jev's score is the fallback. The `jev` run steps record whether the code found release notes as `notesFound`; it stays false when Jev fails. A Jev failure: needs review, `jev-error`.
   The `changelog-present` review body says where the notes came from, first match: the PR body (`with release notes in the PR body`); the fetched sections (`with release notes from`, then one link per source, at most 5, comma-separated: `owner/repo tag` linking its release, or its `CHANGELOG.md` at that tag when the notes came from the file); otherwise Jev's score to two decimals.
   Fetched sections without a recorded source give `bump with changelog.`.

### Merge-from-base commits

A commit that only merges the base branch in is accepted when it adds nothing beyond the base branch's own changes. Renovate will not rebase a pull request while its release-age check is pending, so the owner may press "Update branch". Every read is pinned to the judged head sha, which must be the last listed commit.

1. The commits are one or more Renovate commits (each with one parent, each the child of the one before) followed only by merge commits. No Renovate commit may follow a merge. The last Renovate commit, picked by its place in that chain and not by date, is the one whose message states the versions: `Precheck`, `BuildContext` and the triage result check all read the version table from it, not from the head.
2. A merge commit has two parents. The first is the commit before it in the chain. The second is on the base branch, and the pull request's base is the repository's default branch: the comparison `{base}...{parent}` is `behind` or `identical`. Its author does not matter. At most 10 merge commits are accepted.
3. The files of the comparison from the last Renovate commit to the head must equal the files of the comparison from the branch point to the newest base parent: the same paths, previous names, statuses and blob shas, and none of those paths may be a path the Renovate commits changed. A renamed or copied file, a comparison that lists 300 or more files (the API truncates there), a comparison without a file list, or a file without a blob sha fails. So a merge that resolves a conflict, edits anything else, drops a base change, or sits where the base touched a file Renovate also changed is handed to the owner as `non-renovate-commit`.
4. A definite answer from GitHub (a missing comparison, `404` or `422`) is "not accepted". A transient error (`5xx`, `429`, network) fails the activity so Temporal retries it; no verdict is recorded on it.
5. `Assess` and the `Apply` gate run the same check. A pull request of only Renovate commits behaves as before and reads no comparison. The reads use the installation's `contents: read` token.

### Bump-type guard

Jev stays the judge; the code only guards the diff.

- A `digest` answer approves only when every changed line pair in the pull request diff differs in `sha256` or digest strings alone, or the new line is the old line with `@sha256:<64 hex>` appended (a Renovate pin-digest change). Anything else: needs review, `digest-mismatch`.
- For `patch` and `minor`, the old and new versions are read from the `-` and `+` line pairs of the diff (semver-like versions). If any pair disagrees with Jev's type, or a changed line pair has a different number of versions on its two sides (or a version on one side only): needs review, `bump-type-mismatch`. When the diff has no versions at all, Jev's answer stands.
- The parsed versions also fill the comment texts: a major comment shows only a semver major pair and a calver comment only a calendar-versioned pair (a year such as `2025.1` first). A comment leaves the versions out when no such pair was parsed.

### Writes

`Apply` writes with an installation token for the pull request's repository. It first re-reads the pull request and writes nothing, only logging, when the head sha is not the judged one or the pull request is not open. It has its own retry options (up to 12 tries, about 35 minutes of backoff). Errors `400`, `404`, `410` and `422` are not retried; rate limits (`403` or `429`) and `5xx` are.

Before it writes anything, `Apply` checks the pull request on fresh data at the judged head. A check that fails here leaves the pull request untouched.

- Every verdict: the repository owner, `Live` mode, the pull request still open and the head still the judged one. Otherwise nothing is written and the verdict stays unapplied (`not-live`, `not-open`, `head-moved`, and `ci-pending` for a run started before the `renovate-triage-ci-race-v1` patch).
- An approving verdict (approve or `SAFE`) is re-assessed from scratch: the author must be Renovate, the head and base must be in the repository, and none of `agent/needs-human-review`, `agent/needs-triage`, `agent/revert` or `blocked` may be on the pull request, else `Apply` refuses (`not-renovate`, `blocking-label`, nothing written, the verdict stays unapplied).
  `agent/needs-human-review` refuses every outcome except the needs-review hand-off, which writes that label itself, so its retry completes.
  Every commit must be one Renovate made or a merge-from-base commit (the gate of rule 5, so commits added after the judgement count); a failure is handed to the owner as needs review, `non-renovate-commit`.
  Then the apply-precheck runs again (diff only version and digest updates, body through the scanner), whether or not `Assess` prechecked this head, because the diff and body can change after the verdict: a failure is handed to the owner as needs review with the precheck reason, and a head that moves while it reads aborts with nothing written.
- `SAFE` is then checked against CI: failed checks make it `FIXABLE`; checks still pending (restarted since the verdict) send a run started after the `renovate-triage-ci-race-v1` patch back to waiting for CI on the same 2h clock (waking every five minutes like any CI wait), then it applies or hands off as `ci-timeout`; an earlier run stops with the verdict unapplied (`ci-pending`). The `renovate/stability-days` status is ignored. A branch on the high-risk list is never approved: the label and the comment are written, the review is not, and the comment opens with a line saying the PR is safe but high-risk and needs the owner's approval.
- A flagging verdict (`FIXABLE`, `BLOCKED`, `Owner` or needs review) skips those gates but is refused (`blocking-label`) when the owner has put `blocked` (for every flag but `BLOCKED`), `agent/needs-human-review` (for `FIXABLE` and `BLOCKED`) or `agent/revert` on the pull request meanwhile, or the author is not Renovate. The label a verdict writes itself does not refuse its own retry.

What each verdict writes, after the gates:

- Approve: an `APPROVE` review pinned to the judged commit (skipped when the App already approved that sha) and the label `agent/auto-approved`. If a sticky comment from an earlier needs-review exists, it is edited to say the new commit was auto-approved (see the table).
- `SAFE`: an `APPROVE` review with a fixed body that never carries agent text, the label `agent/safe`, and the agent's comment in the sticky comment.
- `FIXABLE`: the label `agent/fixable` and the comment. A `SAFE` verdict that meets a red head is written as `FIXABLE`, with the comment `**Fixable:** CI failed on <short sha>. Failed: <check names, linked>.` built from the failed checks, not from the agent's text.
- `BLOCKED`: the label `blocked` and the comment.
- Owner and needs review: the label `agent/needs-human-review` and the comment. This is also the hand-off for every failed gate above, with the reason of the gate that failed.
- Every flag (`FIXABLE`, `BLOCKED`, Owner, needs review) dismisses every approval by the App, including one on the same commit. GitHub refusing a dismissal (`422`) is logged as a warning and the rest carries on.
- Whatever was written, the park label `agent/needs-triage` is removed when the pull request carries it.
- One sticky comment per pull request, found by the hidden marker `<!-- agent-platform:renovate-triage -->` on a comment by the App. It is created once and edited for each new head sha, never duplicated.
  Agent text sits in a quoted `**Agent output (unverified)**` block below the header `**Agent verdict: SAFE|FIXABLE|BLOCKED|NEEDS REVIEW**`, capped at 8,000 characters, with HTML, backslashes, brackets, mentions, references and links defused, and a footer with the short head sha, the model and the run.
- When the writes are done the verdict row gets `applied_at`, and its verdict, reason and comment are replaced by the ones written, so a downgrade or hand-off is stored as what was written, not as what the agent said.
- An unapplied verdict is applied again with the App's own approval and the `agent/auto-approved` label ignored, and `agent/needs-human-review` ignored when the verdict is needs review. `blocked`, `agent/revert` and human approvals still skip it, and so does `agent/needs-human-review` when the verdict is approve.

The comments:

| Reason                | Text                                                                                                                            |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------- |
| `ci-failed`           | **Needs review:** CI failed on `<short sha>`. Failed: `<check names, linked>`.                                                  |
| `high-risk`           | **Needs review:** `<matched term>` is on the high-risk list. These always get a human look.                                     |
| `breaking`            | **Needs review:** likely breaking changes (Jev score `<x>`).                                                                    |
| `major-bump`          | **Needs review:** major bump, `<old>` -> `<new>`.                                                                               |
| `calver-bump`         | **Needs review:** calendar-versioned bump, `<old>` -> `<new>`. The version can't show whether it breaks anything.               |
| `other-bump`          | **Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways.                              |
| `unknown-bump`        | **Needs review:** couldn't tell the bump type, or the PR bumps several packages in different ways.                              |
| `no-changelog`        | **Needs review:** no changelog found for this bump.                                                                             |
| `jev-error`           | **Needs review:** the triage check couldn't run (Jev error).                                                                    |
| `bump-type-mismatch`  | **Needs review:** the versions in the diff (`<old>` -> `<new>`) don't match the bump type Jev picked (`<type>`).                |
| `digest-mismatch`     | **Needs review:** marked as a digest bump, but more than a digest changed.                                                      |
| `non-renovate-commit` | **Needs review:** this PR has commits that Renovate didn't make.                                                                |
| `ci-timeout`          | **Needs review:** CI didn't finish within 2 hours.                                                                              |
| `digest`              | Review body: **Auto-approved:** digest bump.                                                                                    |
| `changelog-present`   | Review body: **Auto-approved:** `<patch or minor>` bump with release notes in the PR body.                                      |
| `changelog-present`   | Review body: **Auto-approved:** `<patch or minor>` bump with release notes from `<release or CHANGELOG.md links>`.              |
| `changelog-present`   | Review body: **Auto-approved:** `<patch or minor>` bump, Jev scored the changelog `<score>`.                                    |
| `allowlisted`         | Review body: **Auto-approved:** allowlisted.                                                                                    |
| Sticky edit           | **Auto-approved:** new commit `<short sha>`, `<reason>`. The reason is the review body's text with its first letter lowercased. |
| Dismissal message     | A newer commit needs review.                                                                                                    |

## Run data

Every workflow run is recorded in a PostgreSQL database (`rundata`) through EF Core, so runs can be measured, compared and replayed later.

| Table                      | One row per           | Key columns                                                                                                                                                                                                                                                                                                  |
| -------------------------- | --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `runs`                     | workflow run          | `workflow_id` + `workflow_run_id` (unique), `workflow_type`, `status`, `input_event` and `context` (jsonb), `prompt_hash`, `model`, timings, token and cost totals, nullable `outcome` fields for later ground truth                                                                                         |
| `run_steps`                | step of a run         | `run_id` + `seq` (unique), `name`, `rung` (`Rule`, `Jev`, `Claude`), `input` and `output` (jsonb), `confidence`, `stop_reason`, `prompt_hash`, `model`, tokens, `cost_usd`, timings                                                                                                                          |
| `renovate_triage_verdicts` | pull request head sha | `repo` + `pr_number` + `head_sha` + `mode` (unique), `verdict` (`Approve`, `NeedsReview`, `Safe`, `Fixable`, `Blocked`, `Owner`), `rung` (`Rule`, `Jev`, `Claude`), `reasons` (jsonb), `comment`, `created_at`, `applied_at` (once on GitHub), `action` (`Approve` or `Comment`, what was written to GitHub) |
| `claude_runs`              | Claude run            | `id` (run id, the idempotency key), `repo` + `pr_number` + `head_sha`, `workflow_id`, `attempt`, `reserved_at`, `pod_created_at` (set at finish if the pod start was never marked), `finished_at`, `outcome`, `model`, tokens, `cost_usd`                                                                    |
| `claude_cooldowns`         | scope                 | `scope` (primary key: `global` or a repo), `until`, `cause`; a write never shortens a running cooldown                                                                                                                                                                                                       |
| `renovate_triage_pending`  | pull request          | `repo` + `pr_number` (unique), `head_sha`, `reason`, `status`, `attempts` (failed Claude runs on this head), `created_at`, `opened_at` (when the row was created or its head last moved), `held_at` (while `blocked`), `updated_at`; a head move carries the row and resets `attempts` and `held_at`         |
| `sweeper_state`            | singleton             | `id`, `last_seen_delivered_at`, `last_success_at`; written by the sweeper (see Sweeper)                                                                                                                                                                                                                      |
| `prompts`                  | distinct prompt       | `hash` (SHA-256 of the body, primary key), `name`, `body`; a Claude step stores the prompt template it ran                                                                                                                                                                                                   |

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
time-skipping tests. The `renovate-triage-v141-*` histories were captured from release 1.4.1 while a run waited for CI or for Jev; no test records them, so they stay as committed and prove that runs started by 1.4.1 replay on the current code.
The `renovate-triage-v150-*` and `renovate-triage-v19x-*` histories were exported read-only from the cluster's Temporal namespace (worker identities and pod names scrubbed) and prove that runs started before the code rungs (v150) and before the Claude rung (v19x) replay on the current code.
The other `renovate-triage-live-*` histories (`live-approve`, `live-needs-review`) are synthetic, like the rest.
Every workflow change must sit behind `Workflow.Patched` (`renovate-triage-jev-retry-v1`, `renovate-triage-live-v1`, `renovate-triage-step2-v1`, `renovate-triage-claude-v1`, `renovate-triage-head-v1`, `renovate-triage-fresh-check-v1`, `renovate-triage-ci-race-v1`, `renovate-triage-status-poll-v1`, `renovate-triage-ci-poll-v1`). Re-recording rewrites every history a test names: restore the older ones with `git checkout` so they keep proving the older flow.

The integration tests share one AppHost (Temporal dev server, PostgreSQL, Migrate and both Host roles) through `Aspire.Hosting.Testing`. They run `PingWorkflow` end to end, check its `runs` row, check that the Migrate role finished with exit code 0 and post a signed `check_suite` delivery to the Web role to see the triage workflow schedule its `Assess` activity on the Worker. They need a container
runtime, which GitHub's ubuntu runners have; with Podman, set `ASPIRE_CONTAINER_RUNTIME=podman`.

## Image

```bash
docker build -t agent-platform .
docker run --rm -e AgentPlatform__Role=Web agent-platform
```

The runtime image is the chiseled Ubuntu ASP.NET image running as UID `1654`. It needs no writable root filesystem, so it runs under Pod Security `restricted` with `readOnlyRootFilesystem`. Releases are cut by release-please and published to GHCR by the shared CI.
