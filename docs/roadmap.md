# Traceback Roadmap

The foundation milestone (canonical model, ingestion boundary, persistence,
fixture vertical slice) is complete. The milestones below build on it in the
order that maximizes real-data learning: live connectors first, correlation on
top of their data, investigation last.

## 1. GitHub + GitHub Actions connector

First live connectors, chosen together because one PR/commit chain spans both.

- webhook receivers + REST polling for pull requests and commits;
- workflow runs with head SHA linkage and produced image digests;
- raw webhook payload archiving (first consumer of the deferred evidence sink);
- conflict retry for concurrent identical deliveries;
- rate-limit/backoff strategy and connector scheduling infrastructure
  (hosted pollers, per-connector state cursors).

Exit: a merged PR's commit → run → artifact chain reconstructs from live data.

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
- artifact digest-first identity exercised by real registries.

Exit: "is this ticket deployed?" and "what version is running?" answer from the
real deployment path.

## 4. Correlation and engineering timeline

- unified event stream view per service/ticket/commit built from observations;
- "what happened immediately before X" queries (temporal neighbors in the
  observation log);
- cross-provider gap detection (ticket deployed but never merged, artifact
  running without a known build);
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

## Standing principles for every milestone

- new providers enter only through `Traceback.Connectors.Abstractions` events;
- ingestion stays idempotent, order-independent, and fully evidenced;
- questions are answered deterministically first; AI assists after facts exist;
- no new infrastructure without a concrete requirement.
