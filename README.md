# agent-platform

Temporal workers and webhook intake for homelab agents, in .NET.

One .NET 10 image runs as two deployments. The role is chosen by configuration:

| Role     | Does                                                | Ports                                                  |
| -------- | --------------------------------------------------- | ------------------------------------------------------ |
| `Web`    | ASP.NET minimal API (health endpoints only for now) | `8080` webhooks, `8081` MCP, set by the Web deployment |
| `Worker` | Temporal worker on task queue `agent-platform`      | `8080` health only                                     |

Both roles serve `/health` (all checks) and `/alive` (liveness) through `AgentPlatform.ServiceDefaults`, which also wires OpenTelemetry and HTTP resilience.

## Configuration

| Key                           | Default          | Notes                                              |
| ----------------------------- | ---------------- | -------------------------------------------------- |
| `AgentPlatform:Role`          | none, required   | `Web` or `Worker`; startup fails on anything else  |
| `Temporal:Address`            | `localhost:7233` | Worker role only                                   |
| `Temporal:Namespace`          | `default`        | Worker role only                                   |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset            | Enables the OTLP exporter when set                 |
| `ASPNETCORE_HTTP_PORTS`       | `8080`           | Image default; the Web deployment sets `8080;8081` |

Environment variables use `__` as the separator, for example `AgentPlatform__Role=Worker`.

## Layout

| Path                                | Purpose                                                        |
| ----------------------------------- | -------------------------------------------------------------- |
| `src/AgentPlatform.Host`            | The executable: role selection, web endpoints, Temporal worker |
| `src/AgentPlatform.ServiceDefaults` | Shared OpenTelemetry, health checks and resilience             |
| `src/AgentPlatform.AppHost`         | Aspire AppHost for local development only                      |
| `tests/AgentPlatform.Tests`         | xUnit tests                                                    |

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
