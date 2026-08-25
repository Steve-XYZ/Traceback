using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Traceback.Application.Ingestion;
using Traceback.Application.Queries;
using Traceback.Domain.Entities;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Queries;

/// <summary>
/// Repository-scoped traversals over imported source-control history. Every
/// relationship reported here was established by a stored observation: join
/// edges carry the ingestion sequence of the observation that created them,
/// surfaced as RelationshipEvidence so answers remain reconstructible.
/// Implemented as fixed set-based loads shaped in memory (no lazy loading).
/// </summary>
internal sealed class SourceControlQueries(TracebackDbContext db) : ISourceControlQueries
{
    public async Task<IReadOnlyList<SourceRepositorySummary>> ListRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        const string queryName = "repositories-list";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        try
        {
            var rows = await db.SourceRepositories.AsNoTracking()
                .Where(r => !r.IsPlaceholder)
                .OrderBy(r => r.CreatedByProvider)
                .ThenBy(r => r.Key)
                .ToListAsync(cancellationToken);
            return rows
                .Select(r => new SourceRepositorySummary(
                    r.CreatedByProvider, r.Key, r.FullName, r.Description, r.Visibility, r.DefaultBranch, r.Url))
                .ToList();
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    public async Task<PullRequestContextResult?> GetPullRequestContextAsync(string owner, string repo, int number, CancellationToken cancellationToken = default)
    {
        const string queryName = "pull-request-context";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        activity?.SetTag("traceback.query.repository", $"{owner}/{repo}");
        activity?.SetTag("traceback.query.pr_number", number);
        try
        {
            return await ExecutePullRequestContextAsync(owner, repo, number, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<PullRequestContextResult?> ExecutePullRequestContextAsync(string owner, string repo, int number, CancellationToken ct)
    {
        var repository = await FindRepositoryAsync(owner, repo, ct);
        if (repository is null)
            return null;

        var pr = await db.PullRequests.AsNoTracking()
            .FirstOrDefaultAsync(p => p.SourceRepositoryId == repository.Id && p.Number == number, ct);
        if (pr is null)
            return null;

        var commitsWithEdges = await (
                from j in db.PullRequestCommits.AsNoTracking()
                join c in db.Commits.AsNoTracking() on j.CommitId equals c.Id
                where j.PullRequestId == pr.Id
                orderby c.AuthoredAt ?? c.CommittedAt, c.CommittedAt, c.Sha
                select new { Edge = j, Commit = c })
            .ToListAsync(ct);

        var commitIds = commitsWithEdges.Select(x => x.Commit.Id).Distinct().ToList();

        var runs = commitIds.Count == 0
            ? []
            : await db.WorkflowRuns.AsNoTracking()
                .Where(r => r.CommitId != null && commitIds.Contains(r.CommitId.Value))
                .OrderByDescending(r => r.RunId ?? 0)
                .ThenByDescending(r => r.RunAttempt ?? 0)
                .ToListAsync(ct);

        var runIds = runs.Select(r => r.Id).ToList();

        var artifactsByRun = runIds.Count == 0
            ? []
            : await (
                    from j in db.WorkflowRunArtifacts.AsNoTracking()
                    join a in db.BuildArtifacts.AsNoTracking() on j.BuildArtifactId equals a.Id
                    where runIds.Contains(j.WorkflowRunId)
                    orderby j.WorkflowRunId, a.Name, a.CanonicalKey
                    select new { j.WorkflowRunId, Artifact = a })
                .ToListAsync(ct);

        var artifactIds = artifactsByRun.Select(x => x.Artifact.Id).Distinct().ToList();

        var evidence = await EvidenceLoader.LoadAsync(db,
            workItemIds: null,
            pullRequestIds: [pr.Id],
            commitIds: commitIds,
            workflowRunIds: runIds,
            buildArtifactIds: artifactIds,
            deploymentIds: null,
            cancellationToken: ct);
        var observations = await LoadObservationsAsync(
            commitsWithEdges.Select(x => x.Edge.EstablishedSequence), ct);

        var result = new PullRequestContextResult
        {
            RepositoryKey = repository.Key,
            Number = number,
            PullRequest = ResultMappers.ToNode(pr, evidence),
            Commits = commitsWithEdges
                .Select(x =>
                {
                    observations.TryGetValue(x.Edge.EstablishedSequence, out var obs);
                    return new CommitContext
                    {
                        Commit = ResultMappers.ToNode(x.Commit, evidence),
                        EstablishedBy = ToRelationshipEvidence(obs),
                        WorkflowRuns = runs
                            .Where(r => r.CommitId == x.Commit.Id)
                            .Select(run => new WorkflowRunContext
                            {
                                WorkflowRun = ResultMappers.ToNode(run, evidence),
                                Artifacts = artifactsByRun
                                    .Where(a => a.WorkflowRunId == run.Id)
                                    .Select(a => ResultMappers.ToNode(a.Artifact, evidence))
                                    .ToList(),
                            })
                            .ToList(),
                    };
                })
                .ToList(),
            GeneratedAt = DateTimeOffset.UtcNow,
        };
        return result;
    }

    public async Task<CommitDeliveryContextResult?> GetCommitDeliveryContextAsync(string owner, string repo, string sha, CancellationToken cancellationToken = default)
    {
        const string queryName = "commit-delivery-context";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        activity?.SetTag("traceback.query.repository", $"{owner}/{repo}");
        activity?.SetTag("traceback.query.commit_sha", sha);
        try
        {
            return await ExecuteDeliveryContextAsync(owner, repo, sha, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<CommitDeliveryContextResult?> ExecuteDeliveryContextAsync(string owner, string repo, string sha, CancellationToken ct)
    {
        var repository = await FindRepositoryAsync(owner, repo, ct);
        if (repository is null)
            return null;

        var normalizedSha = sha.Trim().ToLowerInvariant();
        var commit = await db.Commits.AsNoTracking()
            .FirstOrDefaultAsync(c => c.SourceRepositoryId == repository.Id && c.Sha == normalizedSha, ct);
        if (commit is null)
            return null;

        var prsWithEdges = await (
                from j in db.PullRequestCommits.AsNoTracking()
                join p in db.PullRequests.AsNoTracking() on j.PullRequestId equals p.Id
                where j.CommitId == commit.Id
                orderby p.Number ?? int.MaxValue, p.ExternalName
                select new { Edge = j, PullRequest = p })
            .ToListAsync(ct);

        var runs = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.CommitId == commit.Id)
            .OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(r => r.RunId ?? 0)
            .ThenByDescending(r => r.RunAttempt ?? 0)
            .ToListAsync(ct);

        var runIds = runs.Select(r => r.Id).ToList();
        var artifactsByRun = runIds.Count == 0
            ? []
            : await (
                    from j in db.WorkflowRunArtifacts.AsNoTracking()
                    join a in db.BuildArtifacts.AsNoTracking() on j.BuildArtifactId equals a.Id
                    where runIds.Contains(j.WorkflowRunId)
                    orderby j.WorkflowRunId, a.Name, a.CanonicalKey
                    select new { j.WorkflowRunId, Artifact = a })
                .ToListAsync(ct);

        var artifactIds = artifactsByRun.Select(x => x.Artifact.Id).Distinct().ToList();

        var evidence = await EvidenceLoader.LoadAsync(db,
            workItemIds: null,
            pullRequestIds: prsWithEdges.Select(x => x.PullRequest.Id).ToList(),
            commitIds: [commit.Id],
            workflowRunIds: runIds,
            buildArtifactIds: artifactIds,
            deploymentIds: null,
            cancellationToken: ct);
        var observations = await LoadObservationsAsync(prsWithEdges.Select(x => x.Edge.EstablishedSequence), ct);

        return new CommitDeliveryContextResult
        {
            RepositoryKey = repository.Key,
            Sha = normalizedSha,
            Commit = ResultMappers.ToNode(commit, evidence),
            PullRequests = prsWithEdges
                .Select(x =>
                {
                    observations.TryGetValue(x.Edge.EstablishedSequence, out var obs);
                    return new CommitPullRequestLink
                    {
                        PullRequest = ResultMappers.ToNode(x.PullRequest, evidence),
                        EstablishedBy = ToRelationshipEvidence(obs),
                    };
                })
                .ToList(),
            WorkflowRuns = runs
                .Select(run => new WorkflowRunContext
                {
                    WorkflowRun = ResultMappers.ToNode(run, evidence),
                    Artifacts = artifactsByRun
                        .Where(a => a.WorkflowRunId == run.Id)
                        .Select(a => ResultMappers.ToNode(a.Artifact, evidence))
                        .ToList(),
                })
                .ToList(),
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    public async Task<RepositoryChangesResult?> ListChangesAsync(
        string owner, string repo, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor,
        CancellationToken cancellationToken = default)
    {
        const string queryName = "repository-changes";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        activity?.SetTag("traceback.query.repository", $"{owner}/{repo}");
        try
        {
            return await ExecuteListChangesAsync(owner, repo, fromUtc, toUtc, limit, cursor, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<RepositoryChangesResult?> ExecuteListChangesAsync(
        string owner, string repo, DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit, string? cursor, CancellationToken ct)
    {
        var repository = await FindRepositoryAsync(owner, repo, ct);
        if (repository is null)
            return null;

        var hasCursor = ChangesCursorCodec.TryDecode(cursor, out var cursorTime, out var cursorKind, out var cursorId);

        // Each stream contributes at most `limit` candidates past the cursor;
        // merging them preserves the global newest-first page because every
        // entry on the final page must appear among its own stream's top slice.
        var prTask = StreamPullRequestsAsync(repository.Id, fromUtc, toUtc, hasCursor ? cursorKind : null, hasCursor ? cursorTime : null, hasCursor ? cursorId : null, limit, ct);
        var commitTask = StreamCommitsAsync(repository.Id, fromUtc, toUtc, hasCursor ? cursorKind : null, hasCursor ? cursorTime : null, hasCursor ? cursorId : null, limit, ct);
        var runTask = StreamWorkflowRunsAsync(repository.Id, fromUtc, toUtc, hasCursor ? cursorKind : null, hasCursor ? cursorTime : null, hasCursor ? cursorId : null, limit, ct);
        await Task.WhenAll(prTask, commitTask, runTask);

        var entries = prTask.Result.Concat(commitTask.Result).Concat(runTask.Result)
            .OrderByDescending(e => e.OccurredAt)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenByDescending(e => e.EntityId)
            .Take(limit)
            .ToList();

        string? nextCursor = null;
        if (entries.Count == limit)
        {
            var last = entries[^1];
            nextCursor = ChangesCursorCodec.Encode(last.OccurredAt, last.Kind, last.EntityId);
        }

        return new RepositoryChangesResult
        {
            RepositoryKey = repository.Key,
            From = fromUtc,
            To = toUtc,
            Limit = limit,
            Entries = entries,
            NextCursor = nextCursor,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }

    // Keyset helpers: strictly-newer-than-cursor predicates per stream.

    private async Task<List<ChangeEntry>> StreamPullRequestsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, string? cursorKind, DateTimeOffset? cursorTime, Guid? cursorId, int limit, CancellationToken ct)
    {
        var stopAfterCursor = cursorKind == ChangesCursorCodec.KindPullRequest && cursorTime is not null;
        var rows = await db.PullRequests.AsNoTracking()
            .Where(p => p.SourceRepositoryId == repoId)
            .Where(p => (p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt) >= from && (p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt) <= to)
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync(ct);

        IEnumerable<PullRequest> filtered = rows;
        if (stopAfterCursor)
        {
            filtered = rows.SkipWhile(p =>
                (p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt) > cursorTime ||
                ((p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt) == cursorTime && p.Id >= cursorId!.Value));
        }

        return filtered
            .Take(limit)
            .Select(p => new ChangeEntry
            {
                OccurredAt = p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt,
                Kind = ChangesCursorCodec.KindPullRequest,
                EntityId = p.Id,
                PullRequest = new PullRequestChange(p.ExternalName, p.Number, p.Title, p.State, p.Url, p.CreatedAt, p.UpdatedAt, p.MergedAt),
            })
            .ToList();
    }

    private async Task<List<ChangeEntry>> StreamCommitsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, string? cursorKind, DateTimeOffset? cursorTime, Guid? cursorId, int limit, CancellationToken ct)
    {
        var stopAfterCursor = cursorKind == ChangesCursorCodec.KindCommit && cursorTime is not null;
        var rows = await db.Commits.AsNoTracking()
            .Where(c => c.SourceRepositoryId == repoId)
            .Where(c => (c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt) >= from && (c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt) <= to)
            .OrderByDescending(c => c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);

        IEnumerable<Commit> filtered = rows;
        if (stopAfterCursor)
        {
            filtered = rows.SkipWhile(c =>
                (c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt) > cursorTime ||
                ((c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt) == cursorTime && c.Id >= cursorId!.Value));
        }

        return filtered
            .Take(limit)
            .Select(c => new ChangeEntry
            {
                OccurredAt = c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt,
                Kind = ChangesCursorCodec.KindCommit,
                EntityId = c.Id,
                Commit = new CommitChange(c.Sha, c.Message, c.AuthoredAt),
            })
            .ToList();
    }

    private async Task<List<ChangeEntry>> StreamWorkflowRunsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, string? cursorKind, DateTimeOffset? cursorTime, Guid? cursorId, int limit, CancellationToken ct)
    {
        var stopAfterCursor = cursorKind == ChangesCursorCodec.KindWorkflowRun && cursorTime is not null;
        var rows = await db.WorkflowRuns.AsNoTracking()
            .Where(r => r.SourceRepositoryId == repoId)
            .Where(r => (r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt) >= from && (r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt) <= to)
            .OrderByDescending(r => r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt)
            .ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        IEnumerable<WorkflowRun> filtered = rows;
        if (stopAfterCursor)
        {
            filtered = rows.SkipWhile(r =>
                (r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt) > cursorTime ||
                ((r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt) == cursorTime && r.Id >= cursorId!.Value));
        }

        return filtered
            .Take(limit)
            .Select(r => new ChangeEntry
            {
                OccurredAt = r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt,
                Kind = ChangesCursorCodec.KindWorkflowRun,
                EntityId = r.Id,
                WorkflowRun = new WorkflowRunChange(r.ExternalName, r.RunId, r.RunAttempt, r.WorkflowName, r.Status, r.Conclusion, r.StartedAt),
            })
            .ToList();
    }

    private Task<SourceRepository?> FindRepositoryAsync(string owner, string repo, CancellationToken ct)
    {
        var key = $"{owner}/{repo}".ToLowerInvariant();
        return db.SourceRepositories.AsNoTracking()
            .Where(r => r.Key == key)
            .OrderBy(r => r.CreatedByProvider)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<IReadOnlyDictionary<long, Observation>> LoadObservationsAsync(IEnumerable<long> sequences, CancellationToken ct)
    {
        var ids = sequences.Where(s => s > 0).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<long, Observation>();
        var rows = await db.Observations.AsNoTracking()
            .Where(o => ids.Contains(o.Sequence))
            .ToListAsync(ct);
        return rows.ToDictionary(o => o.Sequence);
    }

    private static RelationshipEvidence? ToRelationshipEvidence(Observation? observation) =>
        observation is null
            ? null
            : new RelationshipEvidence(observation.Sequence, observation.Provider, observation.EntityTypeName,
                observation.ExternalKey, observation.OccurredAt, observation.ObservedAt);
}

internal sealed class SyncStateQueries(TracebackDbContext db) : ISyncStateQueries
{
    public async Task<IReadOnlyList<SyncStateView>> GetStatesAsync(string? provider = null, CancellationToken cancellationToken = default)
    {
        var query = db.SyncStates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var prefix = provider + "/";
            query = query.Where(s => s.IntegrationId.StartsWith(prefix));
        }

        var rows = await query
            .OrderBy(s => s.IntegrationId)
            .ThenBy(s => s.ResourceType)
            .ToListAsync(cancellationToken);

        return rows
            .Select(s => new SyncStateView(s.IntegrationId, s.ResourceType, s.Cursor, s.LastSuccessAt, s.LastAttemptAt, s.LastError, s.UpdatedAt))
            .ToList();
    }
}
