# Performance baseline

Numbers for the GitHub synchronization path, measured so that later changes have
something to be compared against — and so that the Phase 1 question "does the
per-event `SaveChanges` become a real problem?" gets an answer instead of an
opinion.

## How to reproduce

```bash
dotnet run -c Release --project tests/Traceback.Benchmark
dotnet run -c Release --project tests/Traceback.Benchmark -- --scale small   # a tenth, for checking how a cost scales
```

The benchmark generates a GitHub-shaped repository, serves it through
`FakeGitHubApiHandler`, and drives the production pipeline end to end: REST
client → connector → normalized events → ingestion → PostgreSQL → query layer.
Only the HTTP transport is fake, so the numbers describe Traceback's own cost
rather than GitHub's latency. Real synchronization is slower by however long
GitHub takes to answer the request counts below.

**Corpus** (deterministic, seeded):

| | count |
|---|---|
| pull requests | 500 (4 commits each, 2/3 merged) |
| commits | 5000 (2000 in pull requests, 3000 on the default branch) |
| workflow runs | 3000, of which 136 have a second attempt → 3137 rows |
| artifacts | 600 (one per fifth run) |
| lookback window | 90 days |

**Environment**: Intel i7-6700HQ (4 cores / 8 threads, 2.6 GHz), 15 GB RAM,
WSL2 on Linux 6.18, PostgreSQL 17-alpine in Docker, .NET 10.0.400, Release
build. Absolute values are machine-specific; the ratios are the point.

## Results

| metric | Phase 1 pipeline | current |
|---|---|---|
| initial sync duration | 367.8 s | **66.0 s** |
| initial sync GitHub API requests | 3723 | **729** |
| no-change sync duration | 0.2 s | 0.2 s |
| no-change sync GitHub API requests | 296 | **66** |
| observations received / applied (initial) | 10638 / 8638 | 10638 / 8638 |
| observations received / applied (no-change) | 840 / 0 | 840 / 0 |
| pull request context, median / p95 | 47.9 / 65.4 ms | 47.9 / 71.1 ms |
| commit delivery context, median / p95 | 31.1 / 80.8 ms | 31.0 / 54.4 ms |
| repository changes (50 entries), median / p95 | 20.8 / 174.2 ms | 18.8 / 72.2 ms |

Per-stream durations of the current initial sync: repository 0.5 s, pull
requests 21.7 s, commits 17.3 s, workflow runs 25.8 s.

**Read the duration column with care.** Repeated runs of the same build on this
machine landed between 57.4 s and 66.0 s — roughly ±15%. The 5.6× drop is far
outside that band and is attributable (see below); the query latencies moved by
less than the noise and should be read as "unchanged". The API request counts
are deterministic and carry no such caveat.

Row counts are identical before and after, which is the point: these are
efficiency changes, not behaviour changes.

| table | after initial sync | after no-change sync |
|---|---|---|
| observations | 8638 | 8638 |
| external_identities | 8638 | 8638 |
| commits | 5000 | 5000 |
| workflow_runs | 3137 | 3137 |
| pull_requests | 500 | 500 |
| pull_request_commits | 2000 | 2000 |
| build_artifacts | 600 | 600 |
| workflow_run_artifacts | 600 | 600 |
| engineers | 50 | 50 |
| source_repositories | 1 | 1 |

**A second sync with no provider changes costs 66 API requests, 0.2 s, and zero
domain writes.** It receives 840 observations from the overlap window and
discards all 840 as duplicates. The observation log does not grow by a row.

## What was slow, and why

### The identity lookup was an eleven-relation join (5.9× on sync duration)

`external_identities` has ten typed foreign keys, one per entity type, with a
CHECK constraint keeping exactly one non-null. The resolver eager-loaded all ten
navigations on every lookup:

```csharp
db.ExternalIdentities
    .Include(i => i.SourceRepository).Include(i => i.WorkItem).Include(i => i.PullRequest)
    /* … seven more … */
    .FirstOrDefaultAsync(i => i.Provider == provider && …);
```

That turns a single-row index seek into an eleven-relation `LEFT JOIN` that
PostgreSQL replans on every call — and the resolver calls it once per distinct
entity in a batch. The cost is a flat ~35 ms per observation, which is why the
1/10-scale corpus took 39.7 s and the full corpus took 367.8 s: **linear, not
quadratic**. A growing change tracker would have shown up as superlinear; it
did not.

The fix is to fetch the identity row alone and load the one entity it actually
points at:

```csharp
var identity = await FindIdentityAsync(provider, entityType, key, ct);   // no Include
var pr = await LoadAsync<PullRequest>(identity?.PullRequestId, ct);      // Find: tracked rows cost nothing
```

Measured immediately after this change and before any other: **367.8 s →
62.4 s**, same rows, same observations. That single edit accounts for
essentially the whole improvement.

This also fixed a correctness bug hiding behind the same code. Artifact
resolution looked up aliases with a *non*-including query and then read
`matchedIdentity?.BuildArtifact`, which is null unless EF happened to have the
artifact tracked already. A digest alias pointing at an artifact whose canonical
key differed would miss and create a duplicate row.

### One artifact request per workflow run (5.1× on API requests)

Artifacts were fetched per run, so 3000 runs cost 3000 requests — 81% of the
initial sync's total, against a GitHub budget of 5000 requests per hour. GitHub
also lists artifacts repository-wide, with each artifact naming its
`workflow_run.id`, which covers the same 3000 runs in 6 pages.

Neither is always cheaper: a 7-day overlap window covering a dozen runs would
pay for the whole repository's artifact retention. The connector spends one
probe request to read `total_count` and takes the cheaper path (details in
[integrations/github.md](integrations/github.md#two-ways-to-fetch-artifacts)).

3723 → 729 requests on the initial sync; 296 → 66 on a no-change sync. Wall
clock barely moved (the fake transport costs almost nothing), which is exactly
why request count is measured separately: against real GitHub this is the
difference between using 74% of an hourly budget on artifact lookups and using
under 2%.

### Per-event `SaveChanges` — the Phase 1 question

Phase 1 flushed once per event so later events in a batch could resolve entities
created by earlier ones. That was already replaced by per-batch memo caches plus
a flush every 200 events before these measurements were taken. The remaining
cost after the identity fix is round trips, not flushes: ~4500 SQL statements
for 865 observations at 1/10 scale, dominated by identity lookups (1353) and
commit natural-key lookups (540) rather than by saves.

The change tracker is now also cleared between resource streams, so a pass over
four streams does not carry stream 1's committed graph through stream 4's change
detection. Its measured effect on duration was inside the run-to-run noise; it
is kept because it also stops a rolled-back batch from being flushed when a
stream failure is recorded, which is a correctness property rather than a
performance one.

## What is still slow, and why it is being left alone

**One request per pull request for its commit membership.** 500 of the 729
initial-sync requests are `GET /repos/{o}/{r}/pulls/{n}/commits`. GitHub has no
bulk equivalent, and that listing is the only authoritative statement of which
commits a pull request contains. The alternative — deriving membership from
base/head SHAs — would be inference presented as evidence, which is the one
thing this project will not do. The cost is accepted and documented.

**~2.5 database round trips per observation.** Identity lookup, natural-key
fallback and edge-existence checks each cost a round trip. Batch-prefetching all
identities for a batch's keys would collapse them into a handful of queries.
Not done: a one-minute one-time import is not a problem worth the risk of
rewriting resolution, which is the most correctness-critical code in the
pipeline. The incremental path — the one that runs repeatedly — is 0.1 s.

**Query latencies are acceptable and unoptimized.** Pull request context is
~48 ms against 5000 commits and 3137 runs. If it grows, the shapes are fixed
set-based loads with indexes already in place on
`(source_repository_id, number)`, `(source_repository_id, sha)` and
`(source_repository_id, started_at)`.

## No performance gates

No test asserts a duration. Wall-clock thresholds on a Testcontainers-backed
suite measure the CI machine's mood, not the code, and a red build nobody trusts
is worse than no signal. The benchmark is run deliberately and its results are
recorded here; when a change moves these numbers, the table gets a new column.
