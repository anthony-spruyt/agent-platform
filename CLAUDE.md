# CLAUDE.md

Temporal workers and GitHub webhook intake for homelab agents, in .NET. `README.md` is also the behavioural spec (config keys, triage rules, run-data tables, webhook results); update it in the same commit as any behaviour change.

## Commands

```bash
dotnet build AgentPlatform.slnx --configuration Release
dotnet test --project tests/AgentPlatform.Tests
dotnet test --project tests/AgentPlatform.Tests --filter-class "*SweeperStoreTests"
dotnet test --project tests/AgentPlatform.Tests --filter-method "*SweeperStoreTests.AnOlderDelivery_NeverMovesTheMarkBackwards"
dotnet format AgentPlatform.slnx --verify-no-changes
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/AgentPlatform.Host --no-build --configuration Release
dotnet ef migrations add <Name> --project src/AgentPlatform.Host --output-dir RunData/Migrations
APPLY_FIXES=none ./lint.sh
```

- CI runs the lint in `ci.yaml`, and everything else above except the single-test filters and `migrations add` in `ci-repo.yaml`, with the integration tests as a separate job. The `ef` check needs the Release build first.
- `--filter-class` and `--filter-method` take wildcards; they are the single-test switches of the Microsoft.Testing.Platform runner that `global.json` selects.
- Warnings are errors (`Directory.Build.props`) and the `src/.editorconfig` style rules are enforced in the build.
- `global.json` is a floor with roll-forward, and its oldest SDK fails the build on a CA1859 error in the integration tests. Use the SDK the `Dockerfile` builds with.
- `tests/AgentPlatform.IntegrationTests` start Temporal and PostgreSQL containers through Aspire, so they need a working container runtime; the README has the command. Unit tests need no containers.
- Package versions live in `Directory.Packages.props`. The `Dockerfile` copies project files one by one, so a new project that Host references needs COPY lines there.

## Architecture

One image, three roles chosen by `AgentPlatform:Role` (`Program.cs`, `AgentPlatformRoles`): `Web` (GitHub webhook intake and the AgentGateway MCP server, on separate ports), `Worker` (Temporal workers) and `Migrate` (applies EF Core migrations, then exits; the Worker never migrates). Each role has its own service registration; anything Web and Worker both need, such as `AddGitHubEventHandlers()`, must be registered in both.

The main flow spans several folders under `src/AgentPlatform.Host`:

1. `GitHub/`: a signed `check_suite` webhook goes through `IGitHubEventRouter` to every `IGitHubEventHandler` that accepts it. `RenovateTriageCheckSuiteHandler` signals `RenovateTriageWorkflow` through `IRenovateTriageSignaler` (Temporal SignalWithStart), one workflow per pull request.
2. `Workflows/` holds the workflows and `RenovateTriage/` the activities they call. The deterministic decisions are in `TriageRules.cs`; GitHub reads and writes go through `IGitHubApiClient`, the LiteLLM "Jev" model through `JevClient`.
3. When the rules cannot settle a pull request, the workflow climbs to a Claude Code pod: `AgentPodStep` runs `KubernetesAgentRunner` on the `agent-pods` task queue (everything else runs on `agent-platform`), after `ClaudeBudget/` reserves a run against the caps.
4. The pod calls back into `Gateway/` over MCP (`submit_result`, `get_triage_context`); the gateway turns the result into a Temporal Update on the waiting workflow.
5. A Temporal Schedule runs `RenovateTriageSweeperWorkflow`, which redrives parked pull requests, redelivers missed webhooks and reconciles open pull requests.

Every run, step and verdict is stored in PostgreSQL (`RunData/`). Workflow code does no I/O: GitHub, Kubernetes and database access sit in activities; the run-data activities are idempotent on their unique keys, so a retry never duplicates a row.

## Rules of the codebase

- Any change to workflow code goes behind `Workflow.Patched`, because runs already in Temporal replay on the new code. `tests/AgentPlatform.Tests/Replay` replays the committed histories in `Replay/Histories` and fails on non-determinism; add or re-record one when a workflow flow changes.
- `RECORD_HISTORIES=1 dotnet test --project tests/AgentPlatform.Tests` rewrites every history a test records; `git checkout` any rewritten one that must keep proving an older flow.
- A change to the EF model needs a migration in the same commit (CI compares the model with the migrations). `RunData/Migrations` is generated code.
- The pull request body, changelog sections and the triage context's CI log tails travel as `ScannedText` from `IUntrustedTextScanner`; consumers check `IsIntact` before using it. The README's Renovate triage section says which text is scanned.
- Unit tests use fakes from `tests/AgentPlatform.Tests/Support` and EF InMemory (`InMemoryRunData`), so behaviour that depends on PostgreSQL is covered only by the integration tests.

## Repository files

- `.xfg.json` lists every file repo-operator syncs. Most are overwritten on each sync, and a hook blocks edits to them. Seed-once files, including `.github/workflows/ci-repo.yaml`, `.gitignore`, `.gitleaks.toml` and `.mega-linter.yml`, are edited here. The `repo-operator` skill says where each kind of change goes.
- `CHANGELOG.md` and `.release-please-manifest.json` belong to release-please. PR titles must be conventional commits with a lowercase subject (the `PR Title` check).
