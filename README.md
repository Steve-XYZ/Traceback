# Traceback

Engineering intelligence: connect tickets, pull requests, commits, builds,
artifacts, and deployments into one canonical model with full provenance, so
"what code implements this ticket?", "is it deployed?", and "what version is
running?" are queries, not investigations.

This repository contains the canonical domain model, a provider-independent
ingestion boundary, PostgreSQL persistence with EF Core, OpenTelemetry
instrumentation, and read-only synchronization of real GitHub repositories:
pull requests, commits, GitHub Actions runs (including rerun attempts) and
Actions artifacts, with per-stream incremental checkpoints and full provenance.

See [docs/architecture.md](docs/architecture.md),
[docs/integrations/github.md](docs/integrations/github.md),
[docs/performance.md](docs/performance.md) and
[docs/roadmap.md](docs/roadmap.md).

## Stack

.NET 10 · ASP.NET Core · PostgreSQL 17 · EF Core 10 · xUnit · OpenTelemetry · Docker Compose

## Run the local stack

```bash
cp .env.example .env      # optional: fill in GitHub credentials
docker compose up --build
```

This starts PostgreSQL and the API. On startup the API applies migrations and
ingests the fixture scenario through the normal connector boundary (idempotent;
safe on every restart). The API listens on http://127.0.0.1:8080 — loopback
only, because it has no authentication (see [Security](#security)).

Try the vertical slice:

```bash
# Work item → PR → commit → run → artifact → deployment chain (with evidence)
curl -s http://localhost:8080/api/work-items/BOS-2268/deployment | jq

# Currently running version of a service in an environment
curl -s http://localhost:8080/api/services/player-manager/environments/staging/current-deployment | jq

# Recent changes in an environment (defaults to the last 24h)
curl -s "http://localhost:8080/api/services/player-manager/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-22T00:00:00Z" | jq

# Re-ingest the fixture through the same boundary (all duplicates skipped)
curl -X POST http://localhost:8080/api/admin/ingest/fixtures

curl -s http://localhost:8080/healthz
# Liveness (process only) and readiness (PostgreSQL dependency):
curl -s http://localhost:8080/healthz/live
curl -s http://localhost:8080/healthz/ready
```

OpenAPI document (development): `http://localhost:8080/openapi/v1.json`.
Set `OTEL_EXPORTER_OTLP_ENDPOINT` for either service to export traces/metrics.

## Synchronize a real GitHub repository

Configure a read-only token and one repository, then trigger a sync. Full
details, including the exact token permissions, are in
[docs/integrations/github.md](docs/integrations/github.md).

```bash
dotnet user-secrets --project src/Traceback.Api set "GitHub:Token" "github_pat_..."
dotnet user-secrets --project src/Traceback.Api set "GitHub:Repositories:0:Owner" "acme"
dotnet user-secrets --project src/Traceback.Api set "GitHub:Repositories:0:Name" "player-manager"

# Initial pass imports the configured lookback window; later passes are incremental.
curl -X POST http://localhost:8080/api/admin/integrations/github/sync/acme/player-manager | jq

# Per-stream checkpoints, last success, last (sanitized) error.
curl -s http://localhost:8080/api/admin/integrations/github/status | jq
```

Then query the imported model — deterministically, no LLM:

```bash
# PR → commits → workflow runs → artifacts, each with the observation behind it
curl -s http://localhost:8080/api/repositories/acme/player-manager/pull-requests/1842 | jq

# commit → containing PRs → runs that executed it → outcomes → provable artifacts
curl -s http://localhost:8080/api/repositories/acme/player-manager/commits/be82d.../delivery-context | jq

# repository change timeline for a window, paginated
curl -s "http://localhost:8080/api/repositories/acme/player-manager/changes?from=2026-08-01T00:00:00Z&limit=50" | jq

curl -s http://localhost:8080/api/repositories | jq
```

Synchronization is read-only: the connector issues nothing but `GET`.

## Security

The API has no authentication or authorization. It serves private repository
data, so:

- compose publishes it on `127.0.0.1:8080` only, not on `0.0.0.0`;
- **application-level authorization is required before any deployment beyond a
  local development machine.** This is tracked in
  [docs/roadmap.md](docs/roadmap.md) as cross-cutting work, not as a finished
  feature;
- GitHub tokens live in user secrets, the environment or a mounted secret file —
  never in source control, never in domain tables, never in logs, traces,
  exception messages or API responses;
- `.env` is git-ignored; `.env.example` documents the variables with empty
  values.

## Development without Docker Compose

```bash
dotnet tool restore

# start only infrastructure
docker compose up -d postgres

export TRACEBACK_CONNECTIONSTRING="Host=localhost;Port=54329;Database=traceback;Username=traceback;Password=traceback"

dotnet ef database update --project src/Traceback.Infrastructure --startup-project src/Traceback.Api

dotnet run --project src/Traceback.Api        # IngestFixturesOnStartup=true in Development? set explicitly below
IngestFixturesOnStartup=true dotnet run --project src/Traceback.Api
```

## Tests

```bash
dotnet test                                   # everything
dotnet test tests/Traceback.Tests.Domain      # pure unit tests
dotnet test tests/Traceback.Tests.GitHub      # connector transport + cursor strategy
dotnet test tests/Traceback.Tests.Integration # Testcontainers PostgreSQL + WebApplicationFactory
```

Integration tests require a running Docker daemon. Each test class gets its own
throwaway database from a shared container; no external state is touched. GitHub
tests never reach the network: `FakeGitHubApiHandler` serves a scriptable
in-memory repository as GitHub REST responses (Link-header pagination, rate
limits, 5xx, route-scoped failures), and everything above the transport is the
production code path.

## Performance benchmark

```bash
dotnet run -c Release --project tests/Traceback.Benchmark
```

Synchronizes a generated GitHub-shaped repository (500 pull requests, ~5000
commits, 3000 workflow runs) through the real pipeline into a throwaway
PostgreSQL container and prints sync durations, API request counts, row growth
and query latencies. Recorded results: [docs/performance.md](docs/performance.md).

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/Traceback.Infrastructure \
  --startup-project src/Traceback.Api \
  --output-dir Persistence/Migrations
```

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | local fallback in Development; required otherwise | PostgreSQL connection |
| `MigrateOnStartup` | `true` in Development; `false` otherwise | apply EF migrations at boot |
| `IngestFixturesOnStartup` | `false` (`true` in compose) | seed the fixture scenario via the ingestion boundary |
| `GitHub__Token` | unset | read-only GitHub token (user secrets / environment) |
| `GitHub__TokenFile` | unset | alternative: path to a mounted secret file |
| `GitHub__ApiBaseUrl` | `https://api.github.com/` | GitHub or GitHub Enterprise API base; trailing `/` is normalized so paths such as `/api/v3` are preserved |
| `GitHub__Repositories__0__Owner` / `__Name` | unset | the repository to synchronize |
| `GitHub__InitialLookbackDays` | `30` | history depth of a repository's first sync |
| `GitHub__IncrementalOverlapDays` | `7` | how far behind each watermark later passes re-inspect |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | enables OTLP trace/metric export |

`/healthz/live` reports process liveness without contacting PostgreSQL.
`/healthz/ready` reports PostgreSQL readiness and returns HTTP 503 when the
database is unavailable. `/healthz` remains an alias for readiness. Health
responses expose status only and never include connection details.

Every GitHub setting is listed in
[docs/integrations/github.md](docs/integrations/github.md#configuration).
