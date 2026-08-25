# Traceback

Engineering intelligence: connect tickets, pull requests, commits, builds,
artifacts, and deployments into one canonical model with full provenance, so
"what code implements this ticket?", "is it deployed?", and "what version is
running?" are queries, not investigations.

This repository contains the foundation milestone: canonical domain model,
provider-independent ingestion boundary, PostgreSQL persistence with EF Core,
a fixture-backed vertical slice, integration tests, and OpenTelemetry
instrumentation. See [docs/architecture.md](docs/architecture.md) and
[docs/roadmap.md](docs/roadmap.md).

## Stack

.NET 10 · ASP.NET Core · PostgreSQL 17 · EF Core 10 · xUnit · OpenTelemetry · Docker Compose

## Run the local stack

```bash
docker compose up --build
```

This starts PostgreSQL and the API. On startup the API applies migrations and
ingests the fixture scenario through the normal connector boundary (idempotent;
safe on every restart). The API listens on http://localhost:8080.

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
```

OpenAPI document (development): `http://localhost:8080/openapi/v1.json`.

Set `OTEL_EXPORTER_OTLP_ENDPOINT` for either service to export traces/metrics.

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
dotnet test tests/Traceback.Tests.Integration # Testcontainers PostgreSQL + WebApplicationFactory
```

Integration tests require a running Docker daemon. Each test class gets its own
throwaway database from a shared container; no external state is touched.

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
| `ConnectionStrings__Default` | localhost compose values | PostgreSQL connection |
| `MigrateOnStartup` | `true` | apply EF migrations at boot |
| `IngestFixturesOnStartup` | `false` (`true` in compose) | seed the fixture scenario via the ingestion boundary |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | enables OTLP trace/metric export |
