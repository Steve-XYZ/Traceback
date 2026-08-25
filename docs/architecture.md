# Traceback Architecture

## The problem

Engineering knowledge is scattered across tools. A ticket lives in Linear, its
fix in a GitHub pull request and commits, its build in GitHub Actions, its
image in a registry, its rollout in deploy tooling, its symptoms in Grafana.
No single system can answer "what code implements this ticket?" or "is this
ticket deployed?" without a human manually joining fragments across tabs.

Traceback ingests facts from these systems into one canonical model with full
provenance, so questions about software delivery become queries instead of
investigations. Later milestones add correlation, telemetry, and an AI
investigator; this document describes the foundation those will stand on:
the canonical model, the ingestion boundary, persistence, and idempotency.

## Architectural boundaries

```
┌──────────────────────────────────────────────────────────────┐
│ Traceback.Api          minimal API host, OTel wiring         │
├──────────────────────────────────────────────────────────────┤
│ Connectors.Fixtures    scripted scenario → normalized events │
│ Connectors.* (future)  github, linear, docker, grafana…      │
├──────────────────────────────────────────────────────────────┤
│ Connectors.Abstractions  *Observed event contracts, IConnector│
├──────────────────────────────────────────────────────────────┤
│ Application            query/read ports + result DTOs,       │
│                        IIngestionService port                │
├──────────────────────────────────────────────────────────────┤
│ Infrastructure         EF Core/Npgsql, migrations, ingestion │
│                        pipeline, query implementations       │
├──────────────────────────────────────────────────────────────┤
│ Domain                 canonical entities, pure policies     │
│                        (no dependencies)                     │
└──────────────────────────────────────────────────────────────┘
```

Rules:

- **Connectors know nothing about persistence.** They translate their provider's
  data into `*Observed` events (`Traceback.Connectors.Abstractions`) and hand
  them to `IIngestionService`. That event vocabulary is the entire contract;
  a future live connector needs no other integration point.
- **The domain has no provider types.** Nothing inside `Domain`, `Application`,
  or `Infrastructure` mentions Linear, GitHub, Docker, or any provider. Provider
  identity survives only as opaque data (`provider`, `external_key`, URLs).
- **Application holds contracts, Infrastructure holds implementations.** Query
  results are shaped by Application DTOs; EF Core specifics (jsonb, Npgsql,
  naming) stay in Infrastructure. This keeps the read/write surfaces honest
  without inventing interfaces nobody implements twice.
- Every project exists because it isolates a real dependency edge, not because
  a diagram said so. There is no generic repository, no unit-of-work wrapper,
  no mediator.

## Canonical entities

`WorkItem`, `PullRequest`, `Commit`, `WorkflowRun`, `BuildArtifact`,
`Deployment`, `DeploymentEnvironment`, `Service`, `ServiceInstance`,
`Engineer`.

Every externally sourced entity carries:

- a client-generated `Guid Id` (never derived from a provider identifier);
- `CreatedByProvider`, `FirstObservedAt`, `LastObservedAt`;
- `IsPlaceholder` — see [out-of-order handling](#how-out-of-order-data-is-handled).

### Relationships: explicit relational modeling, no EAV

Each relationship from the requirements was examined for cardinality and
semantics, then modeled explicitly:

| Relationship | Model |
|---|---|
| WorkItem IMPLEMENTED_BY PullRequest | join table `work_item_pull_requests` (M:N — one PR can close several tickets; stacked PRs implement one ticket) |
| PullRequest CONTAINS Commit | join table `pull_request_commits` (M:N — squash merges produce one commit, merge commits produce many, cherry-picks share commits across PRs) |
| Commit BUILT_BY WorkflowRun | FK `workflow_runs.commit_id` (nullable — runs may be observed before their commit); a commit can be built by many runs |
| WorkflowRun PRODUCES BuildArtifact | join table `workflow_run_artifacts` (matrix builds produce many artifacts; re-runs rebuild one) |
| BuildArtifact DEPLOYED_AS Deployment | FK `deployments.artifact_id` |
| Deployment TARGETS Service / RUNS_IN Environment | FKs `service_id`, `environment_id` |
| ServiceInstance BELONGS_TO Service (+ Environment) | FKs |

A generic relation abstraction (edges as rows: `(from, relation, to)`) would
have made every traversal a self-join over untyped strings, given up foreign-key
integrity, and made cardinality implicit. None of the listed relationships
benefits from that flexibility, so none uses it. If a future requirement needs
open-world relations (e.g., free-form incident timelines), it can be added
alongside — not instead of — the explicit model.

## Identity and provenance

**External identifiers are never primary keys.** Internal GUIDs are; external
identities are mapped in `external_identities`:

- unique `(provider, entity_type, external_key)` — the idempotency anchor;
- typed nullable FK columns per entity type plus a database CHECK constraint
  guaranteeing exactly one non-null FK that matches `entity_type_name`;
- `first_observed_at` / `last_observed_at` per mapping.

Natural keys get their own unique indexes where they are stable and globally
meaningful: `work_items.key` ("BOS-2268"), `commits.sha`,
`services.name`, `environments.name`, `build_artifacts.canonical_key`.
Resolution order when a reference arrives: identity mapping first, natural key
second, creation as placeholder last. This lets two providers reporting the same
commit SHA converge on one row while keeping provider-specific objects (pull
requests, workflow runs) distinct per provider.

Artifacts deserve special mention: container tags move, digests do not. An
artifact's canonical key is fixed at first sight (digest if known, else
`name@version`), and additional identities are registered as aliases when new
identifiers are learned later. References stay stable regardless of which
identifier arrived first.

**Provenance of individual facts** is the append-only `observations` log: every
accepted event is stored once with provider, entity type, external key,
`occurred_at` (source-system time), `observed_at` (receive time), a content
fingerprint, and the canonical event JSON as jsonb. Read APIs surface evidence
per node (`sources[]`: provider, external key, URL, observation window), so
every answer explains where each fact came from. Domain rows are projections of
this log and never store provider-specific shapes.

## Ingestion flow

```
connector.CollectAsync()            # provider data → normalized events
        │
        ▼
IngestionService.IngestAsync()
  ├─ serialize each event canonically (System.Text.Json, web defaults)
  ├─ fingerprint = SHA256("tb.v1|provider|eventType|json")
  ├─ drop intra-batch duplicates by fingerprint
  ├─ BEGIN TRANSACTION
  │   ├─ skip events whose fingerprint already exists in observations
  │   ├─ append Observation row
  │   ├─ apply event (typed applier):
  │   │    resolve-or-create entities via external identity
  │   │    merge scalar fields (non-null wins, nulls preserve)
  │   │    union relationship edges (additive)
  │   │    SaveChanges (so later events resolve earlier ones in-batch)
  │   └─ backfill deployment.IngestedSequence / edge.EstablishedSequence
  └─ COMMIT (whole batch atomically)
```

The batch is atomic: a failure anywhere rolls back everything, so webhook
retries cannot leave partial state. Events are saved incrementally *inside* the
transaction so resolution queries within a batch see earlier creations.

## Idempotency strategy

Two layers:

1. **Event level**: unique index on `observations.fingerprint`. The same
   delivery received twice (webhook redelivery, connector replay, restart) is
   skipped entirely; the second application is a no-op, counted as duplicated.
2. **Entity level**: unique `(provider, entity_type, external_key)` collapses
   duplicate observations onto one domain row. Deployments additionally have a
   natural key — unique `(artifact_id, service_id, environment_id, deployed_at)`
   — so two providers reporting the same rollout converge on one fact.

Re-ingesting an entire scenario is therefore always safe, which is why the API
container re-runs fixture seeding on every start without duplicating anything.

## How out-of-order data is handled

Real sources deliver late and out of order: a deployment webhook fires before
its build run is recorded; a ticket links a PR the GitHub connector has not yet
seen. Three mechanisms make arrival order irrelevant:

1. **Placeholder shells.** When an event references an entity that has not been
   observed, the resolver creates the row immediately — carrying only its
   identity (`IsPlaceholder = true`). Relationship edges attach to the shell, so
   the WorkItem→PullRequest edge written by a Linear event survives even though
   the GitHub PR event arrives later. When the real observation lands, the same
   row absorbs its fields and stops being a placeholder.
2. **Merge semantics.** Incoming non-null scalars overwrite; incoming nulls
   never erase known values (a sparse correction cannot blind us). Timestamps:
   `FirstObservedAt` keeps the minimum, `LastObservedAt` the maximum, regardless
   of arrival order.
3. **Facts carry their own times.** `occurred_at` (source time) is stored as
   data on observations and on domain fields like `deployed_at` /
   `authored_at`; write ordering never depends on it. Corrections win by being
   later observations, not by claiming later source timestamps.

"Currently running version" is **derived**, never stored: the newest deployment
with status `succeeded` for a service/environment, tie-broken deterministically
by ingestion sequence. History and current state can therefore never disagree,
and rollbacks are just older artifacts deployed again.

## Observability

OpenTelemetry is wired from day one:

- traces: `Traceback.Ingestion` (batch, per-event apply with provider/key tags),
  `Traceback.Queries` (each read endpoint), plus ASP.NET Core and HTTP client
  instrumentation;
- metrics: `traceback.ingestion.events_received/_applied/_duplicated`,
  `traceback.ingestion.batch.duration`, `traceback.queries.duration`.

Export activates only when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, so
local development stays dependency-free while deployments can ship telemetry to
any OTLP backend.

## Important trade-offs

- **Single-writer assumption.** Uniqueness is enforced in the database, but the
  pipeline resolves conflicts by sequential application rather than optimistic
  retry loops. Concurrent identical deliveries from multiple instances would
  surface as unique-violation errors rather than silent duplicates — correct but
  noisy; a retry-on-conflict path is straightforward to add when multi-writer
  deployment becomes real.
- **Per-event saves within the transaction** keep in-batch resolution correct at
  the cost of round trips. Batch sizes are small (connector polls/webhooks);
  measured performance is not yet a concern worth optimizing.
- **Derived current version** costs a small indexed scan per query instead of a
  materialized pointer that could drift from history.
- **Placeholder rows** mean counts include unresolved references; APIs mark them
  with `IsPlaceholder` so consumers can distinguish evidence-backed facts from
  forward references.
- **Case normalization** lowercases service/environment names, emails, SHAs, and
  artifact keys at the boundary. Display names retain original casing elsewhere.
- **PostgreSQL traversal** is proven with plain relational joins (set-based loads
  shaped in memory). At milestone scale this is far from any limit; the query
  shapes are documented and deterministic, and a materialized path/graph view
  can be added later without changing the model.

## Decisions intentionally deferred

- **Deletion/tombstone semantics** — how providers signal removal (closed PR
  deleted from a work item, artifact pruned from a registry). Edges are additive
  until tombstones have a concrete producer.
- **Raw provider payload archiving** — connectors currently emit normalized
  events whose JSON is archived in `observations`. A dedicated raw-evidence sink
  (original webhook bodies) awaits the first live connector that produces them.
- **Engineer identity resolution beyond email/name matching** — cross-provider
  engineer deduplication (GitHub login vs Linear email) needs real data to
  design against.
- **Authorization, multi-tenancy, rate limiting** — single-tenant local system
  for now.
- **Live connector scheduling/polling infrastructure** — arrives with the first
  live connector (roadmap item 1).
- **AI investigator, Grafana integration, Telegram interface** — explicitly out
  of scope for this foundation; see roadmap.md.
