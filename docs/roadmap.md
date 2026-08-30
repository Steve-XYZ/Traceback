# Traceback Roadmap

The foundation milestone (canonical model, ingestion boundary, persistence,
fixture vertical slice) and the GitHub connector are complete. The milestones
below build on them in the order that maximizes real-data learning: live
connectors first, correlation on top of their data, investigation last.

## 1. GitHub + GitHub Actions connector — done

Read-only synchronization of one or more GitHub repositories.

Delivered:

- repository-scoped identity (`SourceRepository`): PR numbers, run ids and
  commit SHAs are only unique inside a repository, and the schema now says so;
- pull requests with lifecycle timestamps, merge facts, head/base branches and
  authors; commit membership taken from GitHub's own PR commit listing;
- commits with author/committer identity, message and canonical URL;
- Actions runs keyed by `(repository, run id, attempt)`, so a rerun adds an
  attempt instead of rewriting the previous one;
- Actions artifacts as `BuildArtifact` rows with provider evidence, linked to a
  run only when one attempt is known (the logical-run association remains
  unresolved for reruns);
- incremental synchronization with per-stream checkpoints in `sync_states`,
  a deliberate overlap window, and checkpoint advance only after durable
  ingestion;
- provider-state freshness gating, so a late delivery of an older representation
  cannot revert newer state;
- bounded retries, deliberate rate-limit handling, sanitized errors, secrets
  kept out of logs, traces, exceptions and API responses;
- deterministic read APIs: pull request context, commit delivery context, and a
  paginated repository change timeline, each carrying its evidence;
- OpenTelemetry spans and metrics for the whole sync path, and a repeatable
  performance benchmark (see [performance.md](performance.md)).

Explicitly **not** delivered, and not implied by anything above:

- no link from a workflow run to a container image. GitHub Actions exposes no
  REST evidence for it, so the deployment relationship stays unresolved rather
  than guessed;
- no webhooks or background scheduling. Synchronization is pull-based and
  triggered explicitly;
- no issues, reviews, checks, statuses, releases, branches or deployments;
- no application-level authorization on the API (see the security section of the
  README).

## 2. Linear connector

- issue ↔ PR linking via Linear's GitHub associations and branch-name matching;
- status/state transitions as observations (history of ticket movement);
- engineer identity: Linear emails vs GitHub logins; revisit the deferred
  cross-provider engineer resolution here.

Exit: "what code implements this ticket?" answers from live tickets.

## 3. Docker deployment/runtime connector

- registry events / CD webhooks mapping to `DeploymentObserved`;
- container runtime facts mapping to `ServiceInstanceObserved`
  (the model already supports instances; no producer exists yet);
- artifact digest-first identity exercised by real registries;
- **this is the milestone that closes the workflow-run → image gap** left open by
  milestone 1: a registry that reports which run pushed which digest supplies
  the evidence GitHub does not.

Exit: "is this ticket deployed?" and "what version is running?" answer from the
real deployment path.

## 4. Correlation and engineering timeline

- unified event stream view per service/ticket/commit built from observations;
- "what happened immediately before X" queries (temporal neighbors in the
  observation log);
- cross-provider gap detection (ticket deployed but never merged, artifact
  running without a known build);
- extend the repository change timeline from milestone 1 across providers;
- revisit whether any generic relation abstraction has earned existence by now.

Exit: deterministic engineering timelines without LLMs.

## 5. Grafana telemetry integration

- ingest alert annotations and panel-scoped metric snapshots into the timeline;
- correlate incident windows with deployments from milestone 3;
- Traceback remains the consumer; Grafana is not queried at query time.

Exit: deployment + telemetry share one timeline.

## 6. Investigation engine

The first AI-assisted component — deliberately last among data milestones:

- LLM-driven hypothesis generation over the canonical model and timelines,
  strictly grounded in evidence nodes with provenance (no unverifiable claims);
- read-only access to the same typed queries the API exposes;
- every produced statement links back to observation ids.

Exit: an investigator that explains incidents and cites where each fact came
from.

## 7. Telegram interface

- chat bot exposing the typed queries (deployment status, current version,
  recent changes) plus investigator summaries;
- auth via Telegram identity allowlists; audit every query to the observation
  log.

## 8. Google Docs knowledge integration

- attach design docs/runbooks to services and incidents as first-class
  documents with provenance;
- investigator consumes docs as context alongside timelines.

## Cross-cutting work these milestones will force

- **Authorization.** The API is unauthenticated and bound to loopback in
  development. A shared deployment needs authentication and per-repository
  authorization before anything else on this list ships to more than one machine.
- **Background scheduling.** Manual synchronization is enough for one repository;
  several repositories on a cadence need a scheduler with per-integration
  concurrency limits and rate-limit awareness.
- **Multi-writer ingestion.** Concurrent identical deliveries currently surface
  as unique-constraint violations. A conflict-retry path is straightforward but
  unnecessary while a single process triggers synchronization.
- **Tombstones.** Deleted pull requests, expired artifacts and pruned runs have
  no representation; edges are additive until a provider produces removal
  signals.

## Standing principles for every milestone

- new providers enter only through `Traceback.Connectors.Abstractions` events;
- ingestion stays idempotent, order-independent, and fully evidenced;
- questions are answered deterministically first; AI assists after facts exist;
- unknown beats plausible: a relationship without provider evidence stays
  unresolved rather than inferred;
- no new infrastructure without a concrete requirement.
