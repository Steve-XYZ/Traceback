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

        var position = ChangesCursorCodec.TryDecode(cursor, out var cursorTime, out var cursorKind, out var cursorId)
            ? new ChangesPosition(cursorTime, cursorKind, cursorId)
            : null;

        // The three streams run one after another: they share a single
        // DbContext, which allows exactly one in-flight operation. Each returns
        // at most `limit` candidates positioned after the cursor, so merging
        // them yields the true global page - any entry belonging on this page
        // must appear within its own stream's top slice.
        var candidates = new List<ChangeEntry>(limit * 3);
        candidates.AddRange(await StreamPullRequestsAsync(repository.Id, fromUtc, toUtc, position, limit, ct));
        candidates.AddRange(await StreamCommitsAsync(repository.Id, fromUtc, toUtc, position, limit, ct));
        candidates.AddRange(await StreamWorkflowRunsAsync(repository.Id, fromUtc, toUtc, position, limit, ct));

        var entries = candidates
            .OrderByDescending(e => e.OccurredAt)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenByDescending(e => e.EntityId, ChangesPosition.EntityIdOrder)
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

    private async Task<List<ChangeEntry>> StreamPullRequestsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, ChangesPosition? position, int limit, CancellationToken ct)
    {
        var rows = await TimelinePage.LoadAsync(
            db.PullRequests.AsNoTracking()
                .Where(p => p.SourceRepositoryId == repoId)
                .Select(p => new TimelineRow<PullRequest>
                {
                    OccurredAt = p.UpdatedAt ?? p.CreatedAt ?? p.LastObservedAt,
                    EntityId = p.Id,
                    Entity = p,
                }),
            from, to, ChangesCursorCodec.KindPullRequest, position, limit, ct);

        return rows.ConvertAll(r => new ChangeEntry
        {
            OccurredAt = r.OccurredAt,
            Kind = ChangesCursorCodec.KindPullRequest,
            EntityId = r.EntityId,
            PullRequest = new PullRequestChange(
                r.Entity.ExternalName, r.Entity.Number, r.Entity.Title, r.Entity.State, r.Entity.Url,
                r.Entity.CreatedAt, r.Entity.UpdatedAt, r.Entity.MergedAt),
        });
    }

    private async Task<List<ChangeEntry>> StreamCommitsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, ChangesPosition? position, int limit, CancellationToken ct)
    {
        var rows = await TimelinePage.LoadAsync(
            db.Commits.AsNoTracking()
                .Where(c => c.SourceRepositoryId == repoId)
                .Select(c => new TimelineRow<Commit>
                {
                    OccurredAt = c.AuthoredAt ?? c.CommittedAt ?? c.LastObservedAt,
                    EntityId = c.Id,
                    Entity = c,
                }),
            from, to, ChangesCursorCodec.KindCommit, position, limit, ct);

        return rows.ConvertAll(r => new ChangeEntry
        {
            OccurredAt = r.OccurredAt,
            Kind = ChangesCursorCodec.KindCommit,
            EntityId = r.EntityId,
            Commit = new CommitChange(r.Entity.Sha, r.Entity.Message, r.Entity.AuthoredAt),
        });
    }

    private async Task<List<ChangeEntry>> StreamWorkflowRunsAsync(
        Guid repoId, DateTimeOffset from, DateTimeOffset to, ChangesPosition? position, int limit, CancellationToken ct)
    {
        var rows = await TimelinePage.LoadAsync(
            db.WorkflowRuns.AsNoTracking()
                .Where(r => r.SourceRepositoryId == repoId)
                .Select(r => new TimelineRow<WorkflowRun>
                {
                    OccurredAt = r.StartedAt ?? r.CompletedAt ?? r.LastObservedAt,
                    EntityId = r.Id,
                    Entity = r,
                }),
            from, to, ChangesCursorCodec.KindWorkflowRun, position, limit, ct);

        return rows.ConvertAll(r => new ChangeEntry
        {
            OccurredAt = r.OccurredAt,
            Kind = ChangesCursorCodec.KindWorkflowRun,
            EntityId = r.EntityId,
            WorkflowRun = new WorkflowRunChange(
                r.Entity.ExternalName, r.Entity.RunId, r.Entity.RunAttempt, r.Entity.WorkflowName,
                r.Entity.Status, r.Entity.Conclusion, r.Entity.StartedAt),
        });
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

/// <summary>
/// A decoded position on the repository change timeline. The global order is
/// (occurred descending, kind ordinal ascending, entity id descending); every
/// stream must resume strictly after this point or pages would repeat entries
/// that other streams already emitted.
/// </summary>
internal sealed record ChangesPosition(DateTimeOffset OccurredAt, string Kind, Guid EntityId)
{
    /// <summary>
    /// Guid ordering that matches PostgreSQL's <c>uuid</c> ordering (byte-wise
    /// over the canonical textual form), so a database-side ORDER BY and the
    /// in-memory merge agree on the same prefix. .NET's default Guid comparer
    /// orders by struct field and would disagree.
    /// </summary>
    public static readonly IComparer<Guid> EntityIdOrder =
        Comparer<Guid>.Create((a, b) => string.CompareOrdinal(a.ToString("N"), b.ToString("N")));

    /// <summary>True when the given entry sorts strictly after this position.</summary>
    public bool IsAfter(DateTimeOffset occurredAt, string kind, Guid entityId)
    {
        if (occurredAt != OccurredAt)
            return occurredAt < OccurredAt;
        var byKind = string.CompareOrdinal(kind, Kind);
        if (byKind != 0)
            return byKind > 0;
        return EntityIdOrder.Compare(entityId, EntityId) < 0;
    }
}

/// <summary>One timeline candidate: its ordering key plus the row behind it.</summary>
internal sealed class TimelineRow<TEntity>
{
    public DateTimeOffset OccurredAt { get; init; }
    public Guid EntityId { get; init; }
    public TEntity Entity { get; init; } = default!;
}

internal static class TimelinePage
{
    /// <summary>
    /// Loads at most <paramref name="limit"/> window-filtered rows that sort
    /// after <paramref name="position"/>. Rows strictly older than the cursor
    /// time are bounded in SQL; rows at exactly the cursor time (the tie
    /// bucket, a handful in practice) are filtered in memory so the query never
    /// depends on provider-specific uuid comparison being translatable.
    /// </summary>
    public static async Task<List<TimelineRow<TEntity>>> LoadAsync<TEntity>(
        IQueryable<TimelineRow<TEntity>> source,
        DateTimeOffset from,
        DateTimeOffset to,
        string kind,
        ChangesPosition? position,
        int limit,
        CancellationToken ct)
    {
        var windowed = source.Where(r => r.OccurredAt >= from && r.OccurredAt <= to);

        if (position is not { } cursor)
        {
            return await windowed
                .OrderByDescending(r => r.OccurredAt)
                .ThenByDescending(r => r.EntityId)
                .Take(limit)
                .ToListAsync(ct);
        }

        var older = await windowed
            .Where(r => r.OccurredAt < cursor.OccurredAt)
            .OrderByDescending(r => r.OccurredAt)
            .ThenByDescending(r => r.EntityId)
            .Take(limit)
            .ToListAsync(ct);

        var tied = await windowed
            .Where(r => r.OccurredAt == cursor.OccurredAt)
            .ToListAsync(ct);

        older.AddRange(tied.Where(r => cursor.IsAfter(r.OccurredAt, kind, r.EntityId)));
        return older;
    }
}
