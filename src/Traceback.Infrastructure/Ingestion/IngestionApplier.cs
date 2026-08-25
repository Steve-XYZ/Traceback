using Microsoft.EntityFrameworkCore;
using Traceback.Connectors.Abstractions;
using Traceback.Domain.Entities;
using Traceback.Domain.Policies;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Ingestion;

/// <summary>
/// Applies normalized events to domain rows. Merge rules:
/// - scalar overwrites are gated by provider-state freshness: a representation
///   older than the freshest state already projected never clobbers it
///   (StateFreshnessPolicy); incoming nulls preserve known values;
/// - relationship edges are additive unions regardless of freshness
///   (tombstones are a future concern);
/// - every accepted observation still lands in the evidence log even when its
///   scalars lose the freshness comparison;
/// - the entity stops being a placeholder once any real observation applies.
///
/// Edge and deployment existence checks consult per-batch memo sets before the
/// database so batches can flush in chunks without creating duplicate rows.
/// </summary>
internal sealed class IngestionApplier(TracebackDbContext db, EntityResolver resolver)
{
    // Rows created in the current batch whose ingestion sequence is only known
    // after the first save; backfilled before commit.
    internal readonly List<(Deployment Deployment, Observation Observation)> PendingDeployments = [];
    internal readonly List<(WorkItemPullRequest Edge, Observation Observation)> PendingWorkItemPullRequests = [];
    internal readonly List<(PullRequestCommit Edge, Observation Observation)> PendingPullRequestCommits = [];
    internal readonly List<(WorkflowRunArtifact Edge, Observation Observation)> PendingWorkflowRunArtifacts = [];

    private readonly HashSet<string> _knownEdges = [];
    private readonly Dictionary<string, Deployment> _knownDeployments = [];

    public async Task ApplyAsync(TracebackEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case RepositoryObserved e: await ApplyAsync(e, ct); break;
            case WorkItemObserved e: await ApplyAsync(e, ct); break;
            case PullRequestObserved e: await ApplyAsync(e, ct); break;
            case CommitObserved e: await ApplyAsync(e, ct); break;
            case WorkflowRunObserved e: await ApplyAsync(e, ct); break;
            case BuildArtifactObserved e: await ApplyAsync(e, ct); break;
            case DeploymentObserved e: await ApplyAsync(e, ct); break;
            case ServiceObserved e: await ApplyAsync(e, ct); break;
            case EnvironmentObserved e: await ApplyAsync(e, ct); break;
            case ServiceInstanceObserved e: await ApplyAsync(e, ct); break;
            default:
                throw new NotSupportedException($"Unknown event type {evt.GetType().Name}");
        }
    }

    private async Task ApplyAsync(RepositoryObserved e, CancellationToken ct)
    {
        var repo = await resolver.ResolveRepositoryAsync(e.Provenance.Provider, e.Key, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(repo, e.Provenance.ObservedAt);

        if (!StateFreshnessPolicy.CanApplyScalars(repo.ProviderStateAt, ((IStateFreshness)e).StateUpdatedAt))
            return;

        if (e.FullName is not null) repo.FullName = e.FullName;
        if (e.Owner is not null) repo.Owner = EntityResolver.NormalizeName(e.Owner);
        if (e.Name is not null) repo.Name = EntityResolver.NormalizeName(e.Name);
        if (e.Description is not null) repo.Description = e.Description;
        if (e.Visibility is not null) repo.Visibility = e.Visibility;
        if (e.DefaultBranch is not null) repo.DefaultBranch = e.DefaultBranch;
        if (e.Url is not null) repo.Url = e.Url;

        repo.ProviderStateAt = StateFreshnessPolicy.Merge(repo.ProviderStateAt ?? DateTimeOffset.MinValue, ((IStateFreshness)e).StateUpdatedAt);
    }

    private async Task ApplyAsync(WorkItemObserved e, CancellationToken ct)
    {
        var wi = await resolver.ResolveWorkItemAsync(e.Provenance.Provider, e.Key, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(wi, e.Provenance.ObservedAt);

        if (e.Title is not null) wi.Title = e.Title;
        if (e.Description is not null) wi.Description = e.Description;
        if (e.Status is not null) wi.Status = e.Status;
        if (e.Type is not null) wi.Type = e.Type;
        if (e.Url is not null) wi.Url = e.Url;

        var assignee = await resolver.ResolveEngineerAsync(e.Provenance.Provider, e.Assignee, e.Provenance.ObservedAt, ct);
        if (assignee is not null) wi.AssigneeEngineerId = assignee.Id;

        foreach (var prRef in e.ImplementsByPullRequests)
        {
            var pr = await resolver.ResolvePullRequestAsync(prRef.Provider, prRef.ExternalKey, null, e.Provenance.ObservedAt, ct);
            await EnsureEdgeAsync(db.WorkItemPullRequests, PendingWorkItemPullRequests,
                x => x.WorkItemId == wi.Id && x.PullRequestId == pr.Id,
                $"wipr:{wi.Id}:{pr.Id}",
                () => new WorkItemPullRequest { WorkItemId = wi.Id, PullRequestId = pr.Id },
                ct);
        }
    }

    private async Task ApplyAsync(PullRequestObserved e, CancellationToken ct)
    {
        var pr = await resolver.ResolvePullRequestAsync(e.Provenance.Provider, e.ExternalName, e.Repository, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(pr, e.Provenance.ObservedAt);

        // Scalar overwrites respect provider-state freshness: a late delivery of
        // an older PR snapshot must not revert merged/open state.
        if (StateFreshnessPolicy.CanApplyScalars(pr.ProviderStateAt, ((IStateFreshness)e).StateUpdatedAt))
        {
            if (e.Repository is not null) pr.Repository = e.Repository;
            if (e.Number is not null) pr.Number = e.Number;
            if (e.Title is not null) pr.Title = e.Title;
            if (e.State is not null) pr.State = e.State;
            if (e.Url is not null) pr.Url = e.Url;
            if (e.MergedAt is not null) pr.MergedAt = e.MergedAt;
            if (e.CreatedAt is not null) pr.CreatedAt = e.CreatedAt;
            if (e.ClosedAt is not null) pr.ClosedAt = e.ClosedAt;
            if (e.MergeCommitSha is not null) pr.MergeCommitSha = EntityResolver.NormalizeSha(e.MergeCommitSha);
            if (e.HeadSha is not null) pr.HeadSha = EntityResolver.NormalizeSha(e.HeadSha);
            if (e.HeadBranch is not null) pr.HeadBranch = e.HeadBranch;
            if (e.BaseBranch is not null) pr.BaseBranch = e.BaseBranch;

            pr.ProviderStateAt = StateFreshnessPolicy.Merge(pr.ProviderStateAt ?? DateTimeOffset.MinValue, ((IStateFreshness)e).StateUpdatedAt);

            var author = await resolver.ResolveEngineerAsync(e.Provenance.Provider, e.Author, e.Provenance.ObservedAt, ct);
            if (author is not null) pr.AuthorEngineerId = author.Id;
        }

        // Membership evidence stays additive regardless of freshness.
        foreach (var sha in e.CommitShas)
        {
            var commit = await resolver.ResolveCommitAsync(e.Provenance.Provider, e.Repository, sha, e.Provenance.ObservedAt, ct);
            await EnsureEdgeAsync(db.PullRequestCommits, PendingPullRequestCommits,
                x => x.PullRequestId == pr.Id && x.CommitId == commit.Id,
                $"prc:{pr.Id}:{commit.Id}",
                () => new PullRequestCommit { PullRequestId = pr.Id, CommitId = commit.Id },
                ct);
        }
    }

    private async Task ApplyAsync(CommitObserved e, CancellationToken ct)
    {
        var commit = await resolver.ResolveCommitAsync(e.Provenance.Provider, e.Repository, e.Sha, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(commit, e.Provenance.ObservedAt);

        // Commits are immutable content objects: no freshness gate needed.
        if (e.Repository is not null) commit.Repository = e.Repository;
        if (e.Message is not null) commit.Message = e.Message;
        if (e.AuthoredAt is not null) commit.AuthoredAt = e.AuthoredAt;
        if (e.CommittedAt is not null) commit.CommittedAt = e.CommittedAt;

        var author = await resolver.ResolveEngineerAsync(e.Provenance.Provider, e.Author, e.Provenance.ObservedAt, ct);
        if (author is not null) commit.AuthorEngineerId = author.Id;

        var committer = await resolver.ResolveEngineerAsync(e.Provenance.Provider, e.Committer, e.Provenance.ObservedAt, ct);
        if (committer is not null) commit.CommitterEngineerId = committer.Id;
    }

    private async Task ApplyAsync(WorkflowRunObserved e, CancellationToken ct)
    {
        var run = await resolver.ResolveWorkflowRunAsync(e.Provenance.Provider, e.ExternalName, e.Repository, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(run, e.Provenance.ObservedAt);

        // Status/conclusion transitions are mutable state: gate them on the
        // provider's update timestamp so stale snapshots cannot hide completion.
        if (StateFreshnessPolicy.CanApplyScalars(run.ProviderStateAt, ((IStateFreshness)e).StateUpdatedAt))
        {
            if (e.WorkflowName is not null) run.WorkflowName = e.WorkflowName;
            if (e.RunNumber is not null) run.RunNumber = e.RunNumber;
            if (e.RunId is not null) run.RunId = e.RunId;
            if (e.RunAttempt is not null) run.RunAttempt = e.RunAttempt;
            if (e.TriggerEvent is not null) run.TriggerEvent = e.TriggerEvent;
            if (e.Branch is not null) run.Branch = e.Branch;
            if (e.Url is not null) run.Url = e.Url;
            if (e.Repository is not null) run.Repository = e.Repository;
            if (e.Status is not null) run.Status = e.Status;
            if (e.Conclusion is not null) run.Conclusion = e.Conclusion;
            if (e.StartedAt is not null) run.StartedAt = e.StartedAt;
            if (e.CompletedAt is not null) run.CompletedAt = e.CompletedAt;

            run.ProviderStateAt = StateFreshnessPolicy.Merge(run.ProviderStateAt ?? DateTimeOffset.MinValue, ((IStateFreshness)e).StateUpdatedAt);
        }

        // The head-SHA linkage is provider-stated evidence; keep it additive so
        // a stale status snapshot cannot detach the run from its commit.
        if (e.CommitSha is not null)
        {
            var commit = await resolver.ResolveCommitAsync(e.Provenance.Provider, e.Repository, e.CommitSha, e.Provenance.ObservedAt, ct);
            run.CommitId = commit.Id;
        }

        foreach (var descriptor in e.ProducedArtifacts)
        {
            var artifact = await resolver.ResolveArtifactAsync(e.Provenance.Provider, descriptor, e.Provenance.ObservedAt, ct);
            EntityResolver.MarkObserved(artifact, e.Provenance.ObservedAt);
            await EnsureEdgeAsync(db.WorkflowRunArtifacts, PendingWorkflowRunArtifacts,
                x => x.WorkflowRunId == run.Id && x.BuildArtifactId == artifact.Id,
                $"wra:{run.Id}:{artifact.Id}",
                () => new WorkflowRunArtifact { WorkflowRunId = run.Id, BuildArtifactId = artifact.Id },
                ct);
        }
    }

    private async Task ApplyAsync(BuildArtifactObserved e, CancellationToken ct)
    {
        var artifact = await resolver.ResolveArtifactAsync(e.Provenance.Provider, e.Artifact, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(artifact, e.Provenance.ObservedAt);

        if (e.Artifact.Uri is not null) artifact.Uri = e.Artifact.Uri;
    }

    private async Task ApplyAsync(DeploymentObserved e, CancellationToken ct)
    {
        var service = await resolver.ResolveServiceAsync(e.Provenance.Provider, e.ServiceName, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(service, e.Provenance.ObservedAt);
        var environment = await resolver.ResolveEnvironmentAsync(e.Provenance.Provider, e.EnvironmentName, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(environment, e.Provenance.ObservedAt);
        var artifact = await resolver.ResolveArtifactAsync(e.Provenance.Provider, e.Artifact, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(artifact, e.Provenance.ObservedAt);

        Guid? workflowRunId = null;
        if (e.TriggeredByWorkflowRun is { } runRef)
        {
            var run = await resolver.ResolveWorkflowRunAsync(runRef.Provider, runRef.ExternalKey, null, e.Provenance.ObservedAt, ct);
            workflowRunId = run.Id;
        }

        var observation = CurrentObservation ?? throw new InvalidOperationException("No active observation.");
        var deploymentKey = $"{artifact.Id}|{service.Id}|{environment.Id}|{e.DeployedAt:o}";
        // Memo-first: deployments created earlier in this batch may not be
        // flushed yet, so a plain database query would miss them.
        var deployment = _knownDeployments.TryGetValue(deploymentKey, out var tracked)
            ? tracked
            : await db.Deployments.FirstOrDefaultAsync(d =>
                d.ArtifactId == artifact.Id &&
                d.ServiceId == service.Id &&
                d.EnvironmentId == environment.Id &&
                d.DeployedAt == e.DeployedAt, ct);

        if (deployment is null)
        {
            deployment = new Deployment
            {
                ArtifactId = artifact.Id,
                ServiceId = service.Id,
                EnvironmentId = environment.Id,
                DeployedAt = e.DeployedAt,
                Status = MapStatus(e.Outcome),
                WorkflowRunId = workflowRunId,
                CreatedByProvider = e.Provenance.Provider,
                FirstObservedAt = e.Provenance.ObservedAt,
                LastObservedAt = e.Provenance.ObservedAt,
                IngestedSequence = 0,
            };
            await db.Deployments.AddAsync(deployment, ct);
            PendingDeployments.Add((deployment, observation));
            _knownDeployments.Add(deploymentKey, deployment);

            var identity = new ExternalIdentity
            {
                Provider = e.Provenance.Provider,
                EntityTypeName = ExternalEntityTypes.Deployment,
                ExternalKey = BuildDeploymentKey(service.Name, environment.Name, artifact),
                FirstObservedAt = e.Provenance.ObservedAt,
                LastObservedAt = e.Provenance.ObservedAt,
                DeploymentId = deployment.Id,
            };
            await db.ExternalIdentities.AddAsync(identity, ct);
        }
        else
        {
            EntityResolver.MarkObserved(deployment, e.Provenance.ObservedAt);
            if (e.Outcome is not null && deployment.Status == DeploymentStatus.Unknown)
                deployment.Status = MapStatus(e.Outcome);
            deployment.WorkflowRunId ??= workflowRunId;
        }
    }

    private async Task ApplyAsync(ServiceObserved e, CancellationToken ct)
    {
        var service = await resolver.ResolveServiceAsync(e.Provenance.Provider, e.Name, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(service, e.Provenance.ObservedAt);
        if (e.Description is not null) service.Description = e.Description;
        if (e.Team is not null) service.Team = e.Team;
    }

    private async Task ApplyAsync(EnvironmentObserved e, CancellationToken ct)
    {
        var env = await resolver.ResolveEnvironmentAsync(e.Provenance.Provider, e.Name, e.Provenance.ObservedAt, ct);
        EntityResolver.MarkObserved(env, e.Provenance.ObservedAt);
        if (e.Kind is not null) env.Kind = e.Kind;
    }

    private async Task ApplyAsync(ServiceInstanceObserved e, CancellationToken ct)
    {
        var identity = await FindServiceInstanceIdentityAsync(e.Provenance.Provider, e.ExternalName, ct);
        var instance = identity?.ServiceInstance;

        if (instance is null)
        {
            var service = await resolver.ResolveServiceAsync(e.Provenance.Provider, e.ServiceName, e.Provenance.ObservedAt, ct);
            var environment = await resolver.ResolveEnvironmentAsync(e.Provenance.Provider, e.EnvironmentName, e.Provenance.ObservedAt, ct);
            instance = new ServiceInstance
            {
                ExternalName = e.ExternalName,
                ServiceId = service.Id,
                EnvironmentId = environment.Id,
                CreatedByProvider = e.Provenance.Provider,
                FirstObservedAt = e.Provenance.ObservedAt,
                LastObservedAt = e.Provenance.ObservedAt,
                IsPlaceholder = true,
            };
            await db.ServiceInstances.AddAsync(instance, ct);
            var newIdentity = new ExternalIdentity
            {
                Provider = e.Provenance.Provider,
                EntityTypeName = ExternalEntityTypes.ServiceInstance,
                ExternalKey = e.ExternalName,
                FirstObservedAt = e.Provenance.ObservedAt,
                LastObservedAt = e.Provenance.ObservedAt,
                ServiceInstanceId = instance.Id,
            };
            await db.ExternalIdentities.AddAsync(newIdentity, ct);
        }
        else
        {
            instance.ServiceId = (await resolver.ResolveServiceAsync(e.Provenance.Provider, e.ServiceName, e.Provenance.ObservedAt, ct)).Id;
            instance.EnvironmentId = (await resolver.ResolveEnvironmentAsync(e.Provenance.Provider, e.EnvironmentName, e.Provenance.ObservedAt, ct)).Id;
            EntityResolver.MarkObserved(instance, e.Provenance.ObservedAt);
        }

        if (e.Hostname is not null) instance.Hostname = e.Hostname;
        if (e.StartedAt is not null) instance.StartedAt = e.StartedAt;
        if (e.StoppedAt is not null) instance.StoppedAt = e.StoppedAt;
    }

    private Task<ExternalIdentity?> FindServiceInstanceIdentityAsync(string provider, string externalName, CancellationToken ct) =>
        db.ExternalIdentities
            .Include(i => i.ServiceInstance)
            .FirstOrDefaultAsync(i => i.Provider == provider && i.EntityTypeName == ExternalEntityTypes.ServiceInstance && i.ExternalKey == externalName, ct);

    internal Observation? CurrentObservation { get; set; }

    private async Task EnsureEdgeAsync<TEntity>(
        Microsoft.EntityFrameworkCore.DbSet<TEntity> set,
        List<(TEntity, Observation)> pending,
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        string edgeKey,
        Func<TEntity> create,
        CancellationToken ct) where TEntity : class
    {
        // The per-batch memo set makes repeated edge creation safe across chunked
        // saves: edges created earlier in the same batch may not be flushed yet.
        if (_knownEdges.Contains(edgeKey))
            return;
        var existing = await set.AnyAsync(predicate, ct);
        if (existing)
            return;
        var edge = create();
        await set.AddAsync(edge, ct);
        pending.Add((edge, CurrentObservation ?? throw new InvalidOperationException("No active observation.")));
        _knownEdges.Add(edgeKey);
    }

    internal static string BuildDeploymentKey(string serviceName, string environmentName, BuildArtifact artifact) =>
        $"deployments/{serviceName}/{environmentName}/{artifact.CanonicalKey}/{artifact.Version ?? "unversioned"}";

    internal static DeploymentStatus MapStatus(DeploymentOutcome? outcome)
    {
        if (outcome is null)
            return DeploymentStatus.Unknown;
        return outcome.RawStatus.Trim().ToLowerInvariant() switch
        {
            "succeeded" or "success" or "complete" or "completed" or "done" => DeploymentStatus.Succeeded,
            "failed" or "failure" or "error" => DeploymentStatus.Failed,
            "in_progress" or "in-progress" or "running" or "deploying" or "queued" or "pending" => DeploymentStatus.InProgress,
            _ => DeploymentStatus.Unknown,
        };
    }
}
