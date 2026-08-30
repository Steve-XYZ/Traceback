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
investigator; this document describes what exists: the canonical model, the
ingestion boundary, persistence, idempotency, and the first live connector —
read-only GitHub and GitHub Actions synchronization.

## Architectural boundaries

```
┌──────────────────────────────────────────────────────────────┐
│ Traceback.Api          minimal API host, OTel wiring         │
├──────────────────────────────────────────────────────────────┤
│ Connectors.GitHub      REST client, DTOs, event mapper,      │
│                        per-stream cursors (read-only)        │
│ Connectors.Fixtures    scripted scenario → normalized events │
│ Connectors.* (future)  linear, docker, grafana…              │
├──────────────────────────────────────────────────────────────┤
│ Connectors.Abstractions  *Observed events, IConnector,       │
│                        IRepositorySyncSource                 │
├──────────────────────────────────────────────────────────────┤
│ Application            query/read ports + result DTOs,       │
│                        IIngestionService, IRepositorySynchronizer│
├──────────────────────────────────────────────────────────────┤
│ Infrastructure         EF Core/Npgsql, migrations, ingestion │
│                        pipeline, sync orchestration, queries │
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

`SourceRepository`, `WorkItem`, `PullRequest`, `Commit`, `WorkflowRun`,
`BuildArtifact`, `Deployment`, `DeploymentEnvironment`, `Service`,
`ServiceInstance`, `Engineer`.

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

Natural keys get their own unique indexes where they are stable and meaningful
*at the scope they are unique in*: `work_items.key` ("BOS-2268"),
`services.name`, `environments.name`, `build_artifacts.canonical_key` globally;
commits, pull requests and workflow runs per repository (see
[repository scoping](#repository-scoping) below). Resolution order when a
reference arrives: identity mapping first, natural key second, creation as
placeholder last.

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

## Repository scoping

Real GitHub data broke a Phase 1 assumption: several natural keys are only
unique *inside a repository*. Pull request #42 exists in every repository that
has had 42 pull requests. A workflow run id is unique per repository, not per
GitHub. A commit SHA is content-addressed and can legitimately appear in a fork,
a mirror, or a repository that cherry-picked it — but the pull requests and
workflow runs attached to it are repository-specific and must not leak across
repositories.

`SourceRepository` is the identity boundary that expresses this. It carries the
provider-scoped key (`acme/player-manager`, lowercased), the display name,
owner, description, visibility, default branch and URL. Everything imported from
a repository links to it:

| Table | Unique index | Effect |
|---|---|---|
| `source_repositories` | `(created_by_provider, key)` | one row per provider repository |
| `pull_requests` | `(source_repository_id, number)` where both are non-null | `owner-a/repo-x#42` ≠ `owner-b/repo-y#42` |
| `workflow_runs` | `(source_repository_id, run_id, run_attempt)` where the first two are non-null | run ids never collide across repositories; attempts never overwrite each other |
| `commits` | `(source_repository_id, sha)` | the same SHA in two repositories is two rows with independent relationships |

The external identity keys carry the same scope: a commit's key is
`owner/name@sha`, a pull request's is `owner/name#number`, a run's is
`owner/name/actions/runs/{id}/attempts/{n}`.

**Why commits are per-repository rows rather than one global row per SHA.**
A single global commit row would be defensible — Git object identity really is
global — but it would make `commits → pull requests` and `commits → workflow
runs` ambiguous the moment two repositories share a SHA, and every traversal
would need a repository filter it could silently forget. Duplicating the row
costs storage that a repository-scoped delivery query never has to reason about.
If cross-repository commit correlation is ever needed ("this fix also landed in
the fork"), it is a join on `sha`, which is still indexed.

Partial indexes on nullable `source_repository_id` keep rows produced by
providers with no repository context (the fixture connector, a future Linear
event referencing a PR before GitHub has seen it) valid: PostgreSQL treats NULLs
as distinct, so unscoped rows never collide with scoped ones. The resolver
adopts a repository scope when one is learned, and never migrates a row between
repositories.

**This is not multi-tenancy.** There is no tenant column, no row-level security
and no per-tenant connection routing. Repository scoping fixes an identity bug
that exists with a single organization and two repositories; SaaS isolation is a
different problem and is not solved here.

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
  │   │    merge scalar fields (freshness-gated; nulls preserve)
  │   │    union relationship edges (additive)
  │   ├─ SaveChanges every 200 events (memo caches keep in-batch
  │   │    resolution correct between flushes)
  │   └─ backfill deployment.IngestedSequence / edge.EstablishedSequence
  └─ COMMIT (whole batch atomically)
```

The batch is atomic: a failure anywhere rolls back everything, so a retried
delivery cannot leave partial state.

Phase 1 saved once per event so that later events in a batch could resolve
entities created by earlier ones. Real synchronization batches are thousands of
events, not the handful a fixture produces, and one round trip per event is a
measurable cost at that size. The resolver and the edge/deployment existence
checks now consult per-batch memo caches before touching the database, which
makes in-batch resolution correct without an intermediate flush, so saves happen
every 200 events instead. The enclosing transaction still makes the whole batch
atomic regardless of where the chunk boundaries fall. Measurements are in
[performance.md](performance.md).

## Idempotency strategy

Two layers:

1. **Event level**: unique index on `observations.fingerprint`. The same
   delivery received twice (webhook redelivery, connector replay, restart) is
   skipped entirely; the second application is a no-op, counted as duplicated.
2. **Entity level**: unique `(provider, entity_type, external_key)` collapses
   duplicate observations onto one domain row. Deployments additionally have a
   natural key — unique `(artifact_id, service_id, environment_id, deployed_at)`
   — so two providers reporting the same rollout converge on one fact.

Deployment observations retain the provider's raw external key on the resolved
deployment separately from the synthetic rollout identity, so a provider key
reused for a later rollout remains visible on both observations without
remapping either deployment.

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
2. **Merge semantics.** Incoming non-null scalars overwrite *when they are at
   least as fresh as what is already projected* (see
   [freshness](#freshness-and-stale-write-semantics)); incoming nulls never
   erase known values (a sparse correction cannot blind us). Timestamps:
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

## Freshness and stale-write semantics

Phase 1 merged scalars with "last write wins, nulls preserve". That is wrong
against a real provider. A synchronization overlap window redelivers old
representations by design; a retried request can land after a newer one; two
streams can carry different snapshots of the same object in one pass. Under
last-write-wins, a pull request observed as `merged` at 11:00 reverts to `open`
because a 10:00 representation happened to arrive second.

Arrival order is not a fact about the world. Provider state timestamps are.
`StateFreshnessPolicy` (a pure domain policy) gates scalar writes:

- events describing mutable provider state implement `IStateFreshness` and
  declare the provider's own update time (`pull_request.updated_at`,
  `workflow_run.updated_at`, the repository's `updated_at`);
- each such entity stores the freshest state timestamp it has projected, in
  `provider_state_at`;
- an incoming representation applies its scalars only when its state timestamp
  is **not older** than `provider_state_at`. Equal timestamps still apply, so a
  genuine re-delivery is a harmless no-op rather than a dropped update;
- events with no state timestamp apply as before. A connector that cannot know
  its freshness opts out explicitly rather than having a timestamp invented for
  it;
- deployment lifecycle status is owned by the provider that created the
  deployment row. Other providers' deployment outcomes remain evidence only:
  their clocks are independent and cannot safely overwrite the canonical status;
- commits are exempt: a commit object is immutable content, so there is nothing
  to lose a race over.

Two things are deliberately *not* gated:

- **Relationship edges.** Membership is additive evidence. A stale snapshot that
  no longer lists a commit does not mean the commit left the pull request; it
  means that snapshot is old. Removing edges needs tombstone semantics, which no
  provider produces yet.
- **The observation log.** A stale representation is still evidence that the
  provider said something. It is appended, fingerprinted and queryable even when
  its scalars lose the comparison. `provider_state_at` records what won; the log
  records everything that was said.

## GitHub connector boundary

`Traceback.Connectors.GitHub` is the only assembly that knows GitHub exists.
It contains four pieces:

- `GitHubRestClient` — transport. Authentication header per request, Link-header
  paging, bounded retries with exponential backoff and jitter, deliberate
  rate-limit handling, JSON to internal DTOs. Emits
  `traceback.sync.api_requests`, `.api_retries`, `.rate_limit_events`.
- `Dtos.cs` — the GitHub wire shapes. `internal`, and referenced nowhere else.
- `GitHubEventMapper` — DTO to `*Observed` events. This is where every GitHub
  convention is decided (merged-state derivation, run identity, artifact keys),
  and it is the last place GitHub vocabulary appears.
- `GitHubRepositorySyncSource` — implements `IRepositorySyncSource`: which
  streams exist, how each pages, and what cursor is safe to resume from.

Nothing above the connector references a GitHub type. `IRepositorySyncSource`
speaks in resource-type strings, opaque cursor strings and normalized events;
the orchestrator in `Infrastructure.Sync` treats "github" as a name. Adding
GitLab or Azure DevOps means a new assembly implementing the same port, with no
change to ingestion, persistence or queries.

The client is also the boundary that keeps GitHub read-only: every method issues
`GET`, and there is no code path that constructs any other verb.

## Synchronization architecture

```
POST /api/admin/integrations/github/sync/{owner}/{repo}
        │
        ▼
RepositorySynchronizer.SynchronizeAsync("github", …)      # span: github.sync
  for each resource stream, in order:
    ├─ load SyncState(integration_id, resource_type)      # from PostgreSQL
    ├─ source.FetchAsync(cursor, lookback, now)           # span: github.fetch.<stream>
    │     walks capped top-level and nested pages           # span: traceback.normalize
    ├─ ingestion.IngestAsync(events)                      # span: traceback.ingest
    │     one transaction; idempotent; appends observations
    └─ advance the cursor  ← only after the ingest commits
```

**Streams, not one big download.** Four resource streams run in order:
`repository`, `pull_requests`, `commits`, `workflow_runs`. Each keeps its own
cursor in `sync_states`, keyed by `(integration_id, resource_type)` where
`integration_id` is `github/{owner}/{repo}`. `SyncState` also records
`last_attempt_at`, `last_success_at` and a sanitized `last_error`, which answers
"when did this repository last sync, what succeeded, and what failed".

**The checkpoint boundary is per stream.** A stream is fetched completely,
ingested atomically, and only then does its cursor advance. This is the smallest
boundary that cannot lose data:

- a failure during fetch or ingest leaves that stream's cursor untouched, so the
  next pass refetches exactly the same window;
- streams that already completed keep their advanced cursors, because their data
  is durably committed — re-fetching them would be wasted work, not correctness;
- the run stops at the first failing stream rather than continuing and advancing
  later checkpoints past data that was never fetched.

The partial-failure case in the requirements — pull requests succeed, workflow
runs fail — therefore recovers on the next pass without a manual reset, and
without re-importing the pull requests.

**Cursors are watermarks, not opaque page tokens.** GitHub offers no resumable
cursor across passes, so each stream stores the freshest provider timestamp it
observed and re-derives its request from that. Watermarks advance to provider
timestamps, never to wall-clock time, so clock skew between Traceback and GitHub
cannot skip data.

**The overlap window is deliberate.** Later passes lower the floor by
`IncrementalOverlapDays` (7 by default), because GitHub's filters do not match
what needs observing: a rerun bumps `run_attempt` without moving `created_at`,
artifacts appear after their run finishes, and `commits?since` filters on
committer date, which a rebase can move behind the watermark. Re-inspecting an
overlap costs API requests and produces duplicate observations that idempotency
absorbs; pretending an exact cursor exists would cost data. Per-stream cursor
mechanics are tabulated in
[integrations/github.md](integrations/github.md#incremental-synchronization).

**Truncation never advances a checkpoint.** Each top-level stream, pull-request
commit listing, and per-run artifact listing has its own `MaxPagesPerFetch`
budget. The first page counts once; if a next link remains at the cap, the
source raises a typed page-limit failure before returning that stream's batch.
The watermark and data therefore stay unchanged, and repeating the same capped
request repeats the leading window and fails again because the cursor cannot
move. Raise the cap, or narrow the lookback where it removes the oversized
work, before retrying. The typed failure names the affected nested listing
(for example, `pull_request_commits` or `workflow_run_artifacts`) even when
the owning stream is `pull_requests` or `workflow_runs`. A safety valve cannot
silently become a data-loss valve.

## Workflow rerun modeling

GitHub reruns a workflow by adding an attempt to the same run id. The runs
listing then reports only the latest attempt — the earlier attempt's status,
conclusion and timing are no longer in that response at all. Projecting a run
onto one row per run id would therefore rewrite history: a failed attempt 1
would become "successful" the moment attempt 2 passed, and the evidence that the
first attempt ever failed would be gone.

Traceback keys runs on `(repository, run id, attempt)` instead. Concretely:

- the identity key is `owner/name/actions/runs/98122/attempts/1`;
- when the listing shows `run_attempt > 1`, the connector calls
  `.../attempts` and emits an event per attempt, so attempt 1 is re-observed
  with its own conclusion rather than inferred;
- the unique index `(source_repository_id, run_id, run_attempt)` makes a second
  attempt a new row by construction;
- query results expose `runId` and `runAttempt`, so a caller can distinguish
  "run 98122 failed, then succeeded on retry" from "two unrelated runs".

Artifacts are scoped by GitHub to a run, not to an attempt. The connector
attaches them to the highest attempt observed in that pass, and the edge table
keeps the earlier attempt's edge from a previous pass — so the history reads
"attempt 1 produced drop, attempt 2 produced drop", which is what actually
happened when a rerun rebuilds the same artifact name.

Fetching those artifacts has two possible shapes (per run, or one repository-wide
listing) whose costs differ by orders of magnitude depending on the pass. The
connector measures rather than assumes; see
[performance.md](performance.md#one-artifact-request-per-workflow-run-51-on-api-requests).
The repository-wide listing consumes one page budget; the per-run shape applies
an independent budget to each run. If a nested walk reaches its cap, raising
`MaxPagesPerFetch` is the remediation. Narrowing the lookback can reduce the
number of runs in the pass where applicable, but rerunning with the same cap
does not make progress because the workflow cursor remains unchanged.

## Evidence rules: observed, derived, unknown

Every relationship the API reports is one of three things, and the three are not
interchangeable.

**Observed.** The provider stated it. Each join row carries
`established_sequence`, the ingestion sequence of the observation that created
it, and the read APIs surface that observation as `establishedBy` (provider,
entity type, external key, occurrence and observation times). "PR #1842 contains
commit be82d" resolves to the exact `GET /repos/o/r/pulls/1842/commits` response
that said so. Observed relationships in the GitHub connector:

| Relationship | Provider evidence |
|---|---|
| PullRequest → Commit | the PR's own commit listing, plus the head SHA stated by the PR object |
| WorkflowRun → Commit | the run's `head_sha` |
| WorkflowRun → BuildArtifact | the run's artifact listing |
| PullRequest/Commit/Run → SourceRepository | the endpoint the object was fetched from |
| Commit → Engineer | the commit's author/committer blocks |

**Derived.** A deterministic rule over observed facts, recomputed on read, never
stored as a fact. "Currently running version" is the only one today: the newest
successful deployment, tie-broken by ingestion sequence. Derivations are pure
functions in `Domain.Policies` so the rule is inspectable and testable, and they
are recomputed rather than cached so history and current state cannot disagree.

The one place the connector *interprets* rather than copies is pull request
state: `merged_at` present → `merged`, else `closed`/`draft`/`open`. GitHub's
own `state` field cannot express "merged", so a merged pull request would
otherwise be indistinguishable from an abandoned one. The rule is total,
deterministic and documented, and the underlying `merged_at`, `closed_at` and
raw payload remain in the observation log.

**Unknown.** Everything else. The load-bearing example: a workflow run almost
certainly built a container image, and the image almost certainly became a
deployment. GitHub's REST API exposes no evidence of either, so Traceback stores
the run and the Actions artifacts and leaves `Deployment` unlinked. No
name-matching heuristic, no "the digest is probably the short SHA", no
plausible-looking edge. An empty answer that can be trusted is worth more than a
populated one that cannot, because the entire value of the system is that its
answers are reconstructible.

## Secret handling

- The token is resolved through `IGitHubTokenProvider` from `GitHub:Token`
  (user secrets or environment) or `GitHub:TokenFile` (a mounted secret,
  re-read at most every 30 seconds so rotation needs no restart). No token
  appears in any file in source control; `.env` is git-ignored and
  `.env.example` carries empty values.
- Tokens are never persisted. No domain table, no `sync_states` row and no
  observation payload holds a credential.
- The `Authorization` header is built per request and never logged. Traces
  record a redacted path and a status code; exception messages name the status
  and path only. `GitHubAuthenticationException` deliberately says "check token
  validity and permissions" rather than echoing the response body, which can
  contain request context.
- Provider error text is sanitized before it reaches `sync_states.last_error`:
  single-line, truncated to 512 characters, message only — exception data that
  might carry payload fragments is dropped.
- The admin endpoints return synchronization counts and checkpoint state. They
  never echo configuration.
- An integration test asserts that a failing request's exception message does
  not contain the token, and that the admin sync response body does not either.

The API itself has **no authentication**. Development binds it to loopback
(`127.0.0.1:8080` in compose) and the README says plainly that application-level
authorization is required before any shared deployment. Building an identity
platform is not part of this milestone; quietly exposing private repository data
on a public port would be.

## What changed from the Phase 1 assumptions

Five Phase 1 decisions were re-examined against real GitHub data. Three had to
change, two did not.

| Phase 1 assumption | Verdict | What happened |
|---|---|---|
| Natural keys are globally unique (`commits.sha`, PR external name) | **Broken** | PR numbers, run ids and SHAs are repository-scoped. Added `SourceRepository` and repository-scoped unique indexes. |
| Scalar merge is "non-null wins, last write wins" | **Broken** | Overlap windows redeliver stale representations. Added `IStateFreshness` + `StateFreshnessPolicy` + `provider_state_at`. |
| One `SaveChanges` per event inside the transaction | **Broken at scale** | Fine for fixture batches, not for thousands of events. Memo caches now make in-batch resolution correct without per-event flushes; saves happen every 200 events. See [performance.md](performance.md). |
| Concurrent duplicate ingestion relies on database uniqueness | **Still fine** | Synchronization is manually triggered and single-writer. A concurrent duplicate surfaces as a unique-violation and a rolled-back batch — correct, if noisy. Conflict-retry arrives with background scheduling, not before. |
| Authentication/secrets deferred | **Addressed for the connector, not the API** | The connector has real secret handling (above). The API is still unauthenticated and now explicitly bound to loopback. |

## Observability

OpenTelemetry is wired from day one. Three activity sources:

| Source | Spans |
|---|---|
| `Traceback.Sync` | `github.sync` · `github.fetch.repository` · `github.fetch.pull_requests` · `github.fetch.pull_request_commits` · `github.fetch.commits` · `github.fetch.workflow_runs` · `github.fetch.run_attempts` · `github.fetch.artifacts` · `traceback.normalize` |
| `Traceback.Ingestion` | `traceback.ingest` · `apply-event` |
| `Traceback.Queries` | `query pull-request-context` · `query commit-delivery-context` · `query repository-changes` · … |

Fetch spans wrap only HTTP work and `traceback.normalize` only DTO-to-event
translation, so a trace separates "GitHub was slow" from "mapping was slow" from
"the database was slow".

Metrics:

| Metric | Meaning |
|---|---|
| `traceback.sync.duration` | end-to-end pass duration, tagged by repository |
| `traceback.sync.observations_applied` / `_duplicated` | what a pass actually changed |
| `traceback.sync.failures` | passes that ended in failure, tagged by integration |
| `traceback.sync.api_requests` | GitHub requests sent |
| `traceback.sync.api_retries` | transient failures retried, tagged `network`/`http` |
| `traceback.sync.rate_limit_events` | rate-limit responses encountered |
| `traceback.ingestion.events_received` / `_applied` / `_duplicated` | ingestion outcome by event type |
| `traceback.ingestion.batch.duration` | ingestion batch duration |
| `traceback.queries.duration` | read-query duration, tagged by query name |

Attributes carry repository keys, integration ids, resource types, run ids and
redacted request paths. No attribute carries a credential; the client tags a
redacted path and status code, never headers.

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
- **Chunked saves inside the transaction.** Per-batch memo caches replaced the
  per-event flush so a synchronization batch of thousands of events does not pay
  a round trip each. The caches live exactly as long as the transaction, so they
  cannot serve stale rows to a later batch.
- **One request per pull request for its commit membership.** GitHub has no bulk
  endpoint for it, and the listing is the only authoritative statement of which
  commits a pull request contains. Deriving membership from base/head SHAs
  instead would be inference, not evidence. The cost is real and measured in
  [performance.md](performance.md).
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
- **Authorization and multi-tenancy** — the API is unauthenticated and bound to
  loopback in development. Repository scoping is an identity fix, not tenant
  isolation.
- **Background scheduling** — synchronization is triggered explicitly. A
  scheduler needs per-integration concurrency limits and rate-limit awareness,
  which one manually synchronized repository does not justify.
- **Webhooks and raw payload archiving** — the connector polls. Webhook receipt
  would add a second delivery path and the raw-evidence sink that goes with it.
- **Force-push recovery** — a rewrite older than the overlap window needs a
  checkpoint reset (documented in
  [integrations/github.md](integrations/github.md)); automatic detection would
  need a branch-tip history nobody has asked for.
- **AI investigator, Grafana integration, Telegram interface** — explicitly out
  of scope for this foundation; see roadmap.md.
