# GitHub integration

Traceback reads pull requests, commits, GitHub Actions runs and Actions
artifacts from one or more GitHub repositories and projects them onto the
canonical model. The connector is read-only: no code path in
`Traceback.Connectors.GitHub` issues a request other than `GET`.

## Required permissions

Use a **fine-grained personal access token** scoped to exactly the repositories
you want to synchronize:

| Repository permission | Access | Used for |
|---|---|---|
| Metadata | Read-only | mandatory for every fine-grained token |
| Contents | Read-only | `GET /repos/{o}/{r}/commits` |
| Pull requests | Read-only | `GET /repos/{o}/{r}/pulls`, `.../pulls/{n}/commits` |
| Actions | Read-only | `GET .../actions/runs`, `.../attempts`, `.../artifacts` |

A classic token with the `repo` scope also works but grants far more than
Traceback needs, including write access. Prefer the fine-grained token.

A GitHub App would be the least-privileged option for an organization-wide
install (per-installation tokens, no user coupling, higher rate limits). The
extension point is `IGitHubTokenProvider`: an app-based provider mints and
caches installation tokens and returns the current one, and nothing above it
changes. It is not implemented — no requirement yet justifies the app
registration and JWT flow.

## Configuration

Settings bind from the `GitHub` configuration section
(`GitHubConnectorOptions`):

| Setting | Default | Meaning |
|---|---|---|
| `GitHub:Token` | — | API token. Supply through user secrets or the environment, never a committed file. |
| `GitHub:TokenFile` | — | Alternative: path to a file containing the token (Docker/Kubernetes secret style). Re-read at most every 30 seconds, so rotation needs no restart. |
| `GitHub:ApiBaseUrl` | `https://api.github.com/` | Override for GitHub Enterprise Server. |
| `GitHub:InitialLookbackDays` | `30` | History depth of a repository's first synchronization. |
| `GitHub:IncrementalOverlapDays` | `7` | How far behind its watermark each stream re-inspects on later passes. |
| `GitHub:PageSize` | `100` | `per_page` for every listing request (GitHub's maximum). |
| `GitHub:MaxPagesPerFetch` | `200` | Safety cap per stream per pass. Hitting it leaves the checkpoint unadvanced. |
| `GitHub:MaxRetries` | `3` | Bounded retries for transient failures. |
| `GitHub:RetryBackoffSeconds` | `1.0` | Base of the exponential backoff. |
| `GitHub:MaxRateLimitWaitSeconds` | `120` | Longest in-pipeline wait for a rate-limit reset before failing with the reset time. |
| `GitHub:Repositories[]` | empty | `Owner`, `Name`, optional `InitialLookbackDays`. |

Only repositories listed under `GitHub:Repositories` can be synchronized; the
admin endpoint rejects anything else with 404 rather than reaching out to an
arbitrary repository.

### Local development

The token never belongs in `appsettings*.json`. Use one of:

```bash
# user secrets (per-developer, stored outside the repository)
dotnet user-secrets --project src/Traceback.Api set "GitHub:Token" "github_pat_..."
dotnet user-secrets --project src/Traceback.Api set "GitHub:Repositories:0:Owner" "acme"
dotnet user-secrets --project src/Traceback.Api set "GitHub:Repositories:0:Name" "player-manager"

# or the environment
export GitHub__Token="github_pat_..."
export GitHub__Repositories__0__Owner="acme"
export GitHub__Repositories__0__Name="player-manager"
```

For `docker compose`, copy `.env.example` to `.env` and fill in `GITHUB_TOKEN`,
`GITHUB_OWNER` and `GITHUB_REPO`. `.env` is git-ignored.

### Triggering a synchronization

```bash
curl -X POST http://localhost:8080/api/admin/integrations/github/sync/acme/player-manager | jq
curl -s http://localhost:8080/api/admin/integrations/github/status | jq
```

The sync response reports per-stream counts (`inspected`,
`observationsReceived`, `observationsApplied`, `duplicated`, `cursor`,
`cursorAdvanced`) and never echoes configuration or credentials. The status
endpoint returns stored checkpoints, including the last error message of a
failed stream.

## Supported GitHub objects

| GitHub object | REST endpoint | Canonical entity |
|---|---|---|
| Repository | `GET /repos/{o}/{r}` | `SourceRepository` |
| Pull request | `GET /repos/{o}/{r}/pulls?state=all&sort=updated&direction=desc` | `PullRequest` |
| Pull request commits | `GET /repos/{o}/{r}/pulls/{n}/commits` | `PullRequestCommit` edges + `Commit` |
| Commit | `GET /repos/{o}/{r}/commits?since=…` | `Commit` |
| Workflow run | `GET /repos/{o}/{r}/actions/runs?created=>=…` | `WorkflowRun` |
| Workflow run attempt | `GET /repos/{o}/{r}/actions/runs/{id}/attempts` | one `WorkflowRun` row per attempt |
| Actions artifact | `GET /repos/{o}/{r}/actions/artifacts` or `GET .../runs/{id}/artifacts` | `BuildArtifact`; `WorkflowRunArtifact` when one attempt is known |
| Commit author/committer | embedded in commit and PR payloads | `Engineer` |

Field mapping worth knowing:

- **PR state** is `merged` when `merged_at` is set or `merged` is true, else
  `closed`, else `draft`, else `open`. GitHub's own `state` only distinguishes
  open from closed, so merged pull requests would otherwise be indistinguishable
  from abandoned ones.
- **Run completion** projects `updated_at` onto `CompletedAt` only when
  `status == "completed"`. An in-progress run has no completion time rather than
  a guessed one.
- **Conclusions** are stored verbatim: `success`, `failure`, `cancelled`,
  `timed_out`, `skipped`, `neutral`, `action_required`. Nothing is collapsed
  into a coarser status.
- **Artifacts** get the canonical key `{owner}/{repo}/actions/artifacts/{id}`.
  GitHub's `digest` field is stored as provider-reported archive metadata. It
  is not treated as a container-image digest or used to create an image link.

Every REST request sends `X-GitHub-Api-Version: 2026-03-10`, the currently
supported public GitHub REST contract.

## Initial synchronization

The first pass for a repository has no checkpoint, so each stream uses
`InitialLookbackDays` as its floor:

1. `repository` — one request for identity and metadata.
2. `pull_requests` — walks `pulls?state=all&sort=updated&direction=desc` and
   stops at the first pull request older than the floor. Each pull request
   inside the window also has its commit listing walked; that listing is the
   authoritative membership evidence.
3. `commits` — walks `commits?since={floor}` over the default branch.
4. `workflow_runs` — walks `actions/runs?created=>={floor}`, enumerates all
   attempts of any run whose `run_attempt > 1`, and fetches artifacts for the
   runs in the pass.

An empty initial stream leaves its cursor null. A cursor is written only after
the stream observes an in-window provider timestamp and its events are stored.

Each listing follows the `Link: rel="next"` header while it can still return
items in the stream's lookback window: pull requests stop at the window floor,
and commits/workflow runs use that floor as their provider-side filter. All
walks are also subject to `MaxPagesPerFetch`, so a stream can fail before the
next link is absent. In that case the source reports a
`GitHubPageLimitException`; the partial batch is not ingested and the
checkpoint does not move. Repeating the same request with the same cap repeats
the leading window and fails again. Raise the cap or narrow the requested
lookback before retrying.

### Two ways to fetch artifacts

GitHub lists artifacts per run (`.../runs/{id}/artifacts`) and repository-wide
(`.../actions/artifacts`, where each artifact names its logical `workflow_run.id`).
Which is cheaper depends on the pass, and the difference is large enough to
matter against a 5000-requests-per-hour budget:

- a 90-day first sync covering 3000 runs costs **3000** requests per run, or
  about **6** repository-wide;
- a 7-day overlap window covering 12 runs costs **12** per run, or however many
  pages the repository's whole artifact retention needs — often more.

The connector spends one probe request on the repository listing, reads
`total_count`, and takes the cheaper path: repository-wide when
`ceil(total_count / PageSize) <= min(runs in this pass, MaxPagesPerFetch)`,
per-run otherwise. Both paths produce identical artifact observations and the
same attempt-attribution behavior; the
active choice is on the `github.fetch.artifacts` span as
`traceback.github.artifact_strategy`.

Import the entire repository lifetime by setting a large lookback
(`GitHub:Repositories:0:InitialLookbackDays: 36500`). That is a deliberate
choice, not the default: most repositories carry far more history than is useful
and GitHub charges a request per 100 objects for all of it.

## Incremental synchronization

Each stream stores an opaque JSON cursor in `sync_states`, keyed by
`(integration_id, resource_type)` where `integration_id` is
`github/{owner}/{repo}`.

| Stream | Cursor | Server-side filter | Why |
|---|---|---|---|
| `repository` | `"initialized"` | none | one request; always refetched |
| `pull_requests` | `{"notBefore": …}` newest `updated_at` seen | none available | the list API has no `updated_since`; the walk is sorted by update time and stops at the floor |
| `commits` | `{"since": …}` newest committer date seen | `since` | the API filters by committer date |
| `workflow_runs` | `{"createdFrom": …}` newest `created_at` seen | `created=>=` | the API filters by creation date |

Later passes lower the floor by `IncrementalOverlapDays` (7 by default). This
overlap is deliberate, because GitHub's filters do not match what Traceback
needs to observe:

- a rerun bumps `run_attempt` and `updated_at` but **not** `created_at`, so a
  cursor on `created_at` alone would never see attempt 2;
- artifacts are published after their run finishes, sometimes minutes later;
- `commits?since` filters on committer date, which a rebase or a late merge can
  place behind the watermark.

Re-inspecting the overlap costs API requests but no domain writes: redelivered
objects fingerprint identically and are counted as duplicates. A pass over an
unchanged repository applies zero observations.

Watermarks advance to the freshest **provider** timestamp observed, never to
wall-clock time, so a clock skew between Traceback and GitHub cannot skip data.

### What the overlap does not cover

A force-push that rewrites history further back than the overlap window leaves
the rewritten commits unobserved: they are older than the watermark and GitHub's
`since` filter never returns them. Recovery is a checkpoint reset —
delete the row from `sync_states` and re-synchronize:

```sql
DELETE FROM sync_states WHERE integration_id = 'github/acme/player-manager' AND resource_type = 'commits';
```

Ingestion is idempotent, so a reset re-imports the window without duplicating
anything.

## Failure behaviour

**Checkpoint boundary.** Streams run in order. Each is fetched completely,
ingested in one transaction, and only then does its cursor advance. If the
workflow-run stream fails after the pull-request stream succeeded, the
pull-request checkpoint stays advanced (its data is durable) and the
workflow-run checkpoint keeps its old value, so the next pass refetches exactly
the missing window. The run stops at the first failing stream rather than
pushing on and advancing later checkpoints past data that was never fetched.

**Rate limits.** A `403` with `x-ratelimit-remaining: 0`, or a `429`, is
recognized as a rate limit. If the reset (or `Retry-After`) is within
`MaxRateLimitWaitSeconds`, the client waits once and retries. If the limit
persists after that wait, or the reset is outside the window, it throws
`GitHubRateLimitException` carrying the reset time; the stream records the
error and its checkpoint stays put. There is no hot retry loop. Each event
increments `traceback.sync.rate_limit_events`.

**Transient errors.** Network failures, timeouts and `408/500/502/503/504` are
retried up to `MaxRetries` with exponential backoff plus jitter, counted by
`traceback.sync.api_retries`.

**Permanent errors.** `401`, `403` without a rate-limit signal, and `404` are
never retried; a bad token or a missing repository does not fix itself. The
exception message names the status and the sanitized path, never the token.

## What Traceback does not claim

The point of the connector is to import evidence, not to infer a story around
it. Deliberately absent:

- **No container image linkage.** GitHub Actions exposes no REST evidence that a
  workflow run pushed a particular image digest. An Actions archive digest is
  not an image digest. Workflow evidence is stored and the deployment
  relationship stays unresolved. A registry or deployment connector will
  supply it.
- **No artifact content.** Artifact archives are not downloaded; only the
  metadata GitHub lists (id, name, size, archive digest, download URL, expiry).
  Expired artifacts drop out of GitHub's listings, so an old run may keep an
  artifact edge from an earlier pass while GitHub no longer reports the
  artifact.
- **No inference of which pull request "introduced" a commit** beyond what
  GitHub's own pull-request commit listing states.
- **No issues, reviews, comments, checks, statuses, releases, tags, branches or
  deployments.** They may map cleanly later; none is imported today.
- **No webhooks.** Synchronization is pull-based and manually triggered.
- **No writes.** Nothing in the connector can modify GitHub state.

## Validated against a real repository

Run against one real **private** repository (48 pull requests, 125 default-branch
commits in the window, 188 workflow runs, 176 Actions artifacts) with a
120-day lookback:

| pass | inspected | applied | duplicates | duration |
|---|---|---|---|---|
| initial | 362 | 375 | 81 | 29 s |
| second, no upstream changes | 17 | 0 | 20 | 3.3 s |
| third, after restarting the process | 17 | 0 | 20 | 4.0 s |

What the passes showed:

- reconstruction matches the provider. A merged pull request's three commits and
  their six workflow runs (with real `failure`/`success` conclusions) were
  identical to `gh api repos/{o}/{r}/pulls/{n}/commits` and the runs listing for
  those SHAs;
- every reported PR ↔ commit membership carried the observation sequence that
  established it;
- the second pass advanced no cursor, applied no observation, and left the
  observation count unchanged;
- checkpoints survived a full process restart — the third pass resumed from the
  stored watermarks rather than re-importing;
- eight runs had a second attempt; both attempts kept their own rows;
- 177 run → artifact edges were created from Actions artifact evidence. No
  container image or deployment was inferred from any of them;
- the database also held fixture data from an earlier session, including a pull
  request `acme/player-manager#1842`. It did not collide with the real
  repository's pull requests — which is repository scoping doing its job against
  live data rather than a test fixture.

Nothing in GitHub was modified; every request was a `GET`.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `GitHub access is not configured` | neither `GitHub:Token` nor `GitHub:TokenFile` resolved | set the user secret or environment variable |
| 404 from the admin sync endpoint | the repository is not in `GitHub:Repositories` | add it to configuration and restart |
| `GitHub resource does not exist` | the token cannot see the repository, or owner/name are wrong | check the token's repository access list |
| `GitHub rejected the request (403)` | missing permission (commonly Actions: Read-only) | grant the permission listed above |
| `GitHub rate limit reached; resets at …` | primary or secondary limit hit | wait for the reset, or raise `MaxRateLimitWaitSeconds` |
| Stream reports a page cap | the window holds more than `MaxPagesPerFetch × PageSize` objects | raise the cap or narrow the lookback; repeating with the same cap fails again |
| Second sync applies observations for unchanged data | the repository genuinely changed, or the fake/real clock moved a timestamp | compare `observationsApplied` per stream in the response |

Per-stream state, including the last sanitized error, is available at
`GET /api/admin/integrations/github/status`.
