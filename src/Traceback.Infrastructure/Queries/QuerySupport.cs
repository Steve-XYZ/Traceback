using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Traceback.Application.Queries;
using Traceback.Domain.Entities;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Queries;

internal static class QueryTracing
{
    internal static readonly ActivitySource Activity = new("Traceback.Queries");
    internal static readonly Meter Meter = new("Traceback.Queries");

    private static readonly Histogram<double> QueryDuration =
        Meter.CreateHistogram<double>("traceback.queries.duration", unit: "ms", description: "Read-query execution duration.");

    public static Activity? Start(string queryName)
    {
        var activity = Activity.StartActivity($"query {queryName}");
        activity?.SetTag("traceback.query.name", queryName);
        return activity;
    }

    public static void Record(string queryName, long startTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp, Stopwatch.GetTimestamp()).TotalMilliseconds;
        QueryDuration.Record(elapsedMs, new KeyValuePair<string, object?>("query.name", queryName));
    }
}

/// <summary>Bulk loader for external identities backing result nodes.</summary>
internal sealed class EvidenceLoadResult
{
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> WorkItems { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> PullRequests { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> Commits { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> WorkflowRuns { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> BuildArtifacts { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<ExternalIdentity>> Deployments { get; init; } =
        new Dictionary<Guid, List<ExternalIdentity>>();
    public IReadOnlyDictionary<Guid, List<SourceEvidence>> DeploymentObservations { get; init; } =
        new Dictionary<Guid, List<SourceEvidence>>();

    public static readonly EvidenceLoadResult Empty = new();
}

internal static class EvidenceLoader
{
    public static Task<EvidenceLoadResult> LoadAsync(
        TracebackDbContext db,
        IReadOnlyCollection<Guid>? workItemIds = null,
        IReadOnlyCollection<Guid>? pullRequestIds = null,
        IReadOnlyCollection<Guid>? commitIds = null,
        IReadOnlyCollection<Guid>? workflowRunIds = null,
        IReadOnlyCollection<Guid>? buildArtifactIds = null,
        IReadOnlyCollection<Guid>? deploymentIds = null,
        CancellationToken cancellationToken = default)
    {
        return LoadAll();

        async Task<EvidenceLoadResult> LoadAll() => new()
        {
            WorkItems = await Load(db, x => x.WorkItemId, workItemIds, cancellationToken),
            PullRequests = await Load(db, x => x.PullRequestId, pullRequestIds, cancellationToken),
            Commits = await Load(db, x => x.CommitId, commitIds, cancellationToken),
            WorkflowRuns = await Load(db, x => x.WorkflowRunId, workflowRunIds, cancellationToken),
            BuildArtifacts = await Load(db, x => x.BuildArtifactId, buildArtifactIds, cancellationToken),
            Deployments = await Load(db, x => x.DeploymentId, deploymentIds, cancellationToken),
            DeploymentObservations = await LoadDeploymentObservations(db, deploymentIds, cancellationToken),
        };
    }

    /// <summary>Loads identities pointing at any of the given entity ids, grouped by entity.</summary>
    private static async Task<IReadOnlyDictionary<Guid, List<ExternalIdentity>>> Load(
        TracebackDbContext db,
        System.Linq.Expressions.Expression<Func<ExternalIdentity, Guid?>> foreignKey,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken ct)
    {
        if (ids is not { Count: > 0 })
            return new Dictionary<Guid, List<ExternalIdentity>>();

        var idList = ids.Distinct().ToList();
        var predicate = ExpressionPredicate(foreignKey, idList);
        var rows = await db.ExternalIdentities.AsNoTracking()
            .Where(predicate)
            .ToListAsync(ct);

        var accessor = foreignKey.Compile();
        var grouped = rows.GroupBy(accessor.Invoke)
            .ToDictionary(
                g => g.Key!.Value,
                g => g.OrderBy(i => i.Provider, StringComparer.Ordinal)
                    .ThenBy(i => i.ExternalKey, StringComparer.Ordinal)
                    .ToList());
        return grouped;
    }

    private static async Task<IReadOnlyDictionary<Guid, List<SourceEvidence>>> LoadDeploymentObservations(
        TracebackDbContext db,
        IReadOnlyCollection<Guid>? ids,
        CancellationToken ct)
    {
        if (ids is not { Count: > 0 })
            return new Dictionary<Guid, List<SourceEvidence>>();

        var idList = ids.Distinct().ToList();
        var rows = await db.Observations.AsNoTracking()
            .Where(o => o.EntityTypeName == ExternalEntityTypes.Deployment
                        && o.DeploymentId != null
                        && idList.Contains(o.DeploymentId.Value))
            .ToListAsync(ct);

        return rows
            .GroupBy(o => o.DeploymentId!.Value)
            .ToDictionary(
                deployment => deployment.Key,
                deployment => deployment
                    .GroupBy(o => (o.Provider, o.ExternalKey))
                    .OrderBy(source => source.Key.Provider, StringComparer.Ordinal)
                    .ThenBy(source => source.Key.ExternalKey, StringComparer.Ordinal)
                    .Select(source => new SourceEvidence(
                        source.Key.Provider,
                        source.Key.ExternalKey,
                        null,
                        source.Min(o => o.ObservedAt),
                        source.Max(o => o.ObservedAt)))
                    .ToList());
    }

    private static System.Linq.Expressions.Expression<Func<ExternalIdentity, bool>> ExpressionPredicate(
        System.Linq.Expressions.Expression<Func<ExternalIdentity, Guid?>> foreignKey,
        List<Guid> idList)
    {
        var parameter = foreignKey.Parameters[0];
        var body = Expression.AndAlso(
            Expression.NotEqual(foreignKey.Body, Expression.Constant(null, typeof(Guid?))),
            Expression.Call(
                Expression.Constant(idList),
                IdsContainsMethod,
                Expression.Convert(foreignKey.Body, typeof(Guid))));
        return Expression.Lambda<Func<ExternalIdentity, bool>>(body, parameter);
    }

    private static readonly System.Reflection.MethodInfo IdsContainsMethod =
        typeof(List<Guid>).GetMethod(nameof(List<Guid>.Contains), [typeof(Guid)])!;
}

/// <summary>Mappers from domain entities to result nodes with attached evidence.</summary>
internal static class ResultMappers
{
    public static WorkItemNode ToNode(WorkItem e, EvidenceLoadResult evd) => new()
    {
        Key = e.Key,
        Title = e.Title,
        Status = e.Status,
        Type = e.Type,
        Url = e.Url,
        Sources = Sources(e.Id, e.Url, evd.WorkItems),
    };

    public static PullRequestNode ToNode(PullRequest e, EvidenceLoadResult evd) => new()
    {
        ExternalName = e.ExternalName,
        Number = e.Number,
        Repository = e.Repository,
        Title = e.Title,
        State = e.State,
        Url = e.Url,
        MergedAt = e.MergedAt,
        Sources = Sources(e.Id, e.Url, evd.PullRequests),
    };

    public static CommitNode ToNode(Commit e, EvidenceLoadResult evd) => new()
    {
        Sha = e.Sha,
        Message = e.Message,
        Repository = e.Repository,
        AuthoredAt = e.AuthoredAt,
        Sources = Sources(e.Id, null, evd.Commits),
    };

    public static WorkflowRunNode ToNode(WorkflowRun e, EvidenceLoadResult evd) => new()
    {
        ExternalName = e.ExternalName,
        WorkflowName = e.WorkflowName,
        RunNumber = e.RunNumber,
        Status = e.Status,
        Conclusion = e.Conclusion,
        StartedAt = e.StartedAt,
        CompletedAt = e.CompletedAt,
        Sources = Sources(e.Id, null, evd.WorkflowRuns),
    };

    public static ArtifactNode ToNode(BuildArtifact e, EvidenceLoadResult evd) => new()
    {
        Name = e.Name,
        Version = e.Version,
        Digest = e.Digest,
        Uri = e.Uri,
        Sources = Sources(e.Id, e.Uri, evd.BuildArtifacts),
    };

    public static DeploymentNode ToNode(Deployment d, EvidenceLoadResult evd) => new()
    {
        Id = d.Id,
        DeployedAt = d.DeployedAt,
        Status = d.Status.ToString().ToLowerInvariant(),
        ServiceName = d.Service.Name,
        EnvironmentName = d.Environment.Name,
        IsPlaceholder = d.IsPlaceholder,
        Sources = DeploymentSources(d.Id, evd),
    };

    private static List<SourceEvidence> DeploymentSources(Guid deploymentId, EvidenceLoadResult evd)
    {
        // New observations retain the provider's raw key and are authoritative
        // for API evidence. Synthetic ExternalIdentity keys remain a fallback
        // for deployments created before the observation link was introduced.
        if (evd.DeploymentObservations.TryGetValue(deploymentId, out var observations)
            && observations.Count > 0)
            return observations;
        return Sources(deploymentId, null, evd.Deployments);
    }

    private static List<SourceEvidence> Sources(
        Guid entityId, string? url, IReadOnlyDictionary<Guid, List<ExternalIdentity>> map)
    {
        if (!map.TryGetValue(entityId, out var identities))
            return [];
        return identities
            .Select(i => new SourceEvidence(i.Provider, i.ExternalKey, url, i.FirstObservedAt, i.LastObservedAt))
            .ToList();
    }
}
