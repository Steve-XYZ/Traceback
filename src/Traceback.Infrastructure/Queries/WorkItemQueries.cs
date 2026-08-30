using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Traceback.Application.Queries;
using Traceback.Domain.Policies;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Queries;

/// <summary>
/// Work-item → pull request → commit → workflow run → artifact → deployment
/// traversal. Implemented as a fixed set of set-based relational loads shaped in
/// memory: every hop is an explicit foreign-key or join-table path.
/// </summary>
internal sealed class WorkItemQueries(TracebackDbContext db) : IWorkItemQueries
{
    public async Task<WorkItemDeploymentResult?> GetDeploymentChainAsync(string key, CancellationToken cancellationToken = default)
    {
        const string queryName = "work-item-deployment-chain";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        activity?.SetTag("traceback.query.work_item_key", key);
        try
        {
            return await ExecuteAsync(key, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<WorkItemDeploymentResult?> ExecuteAsync(string key, CancellationToken ct) =>
        await ExecuteCoreAsync(key.Trim(), ct);

    private async Task<WorkItemDeploymentResult?> ExecuteCoreAsync(string normalized, CancellationToken ct)
    {
        var workItem = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Key == normalized, ct);
        if (workItem is null)
            return null;

        var pullRequests = await (
                from j in db.WorkItemPullRequests.AsNoTracking()
                join p in db.PullRequests.AsNoTracking() on j.PullRequestId equals p.Id
                where j.WorkItemId == workItem.Id
                orderby p.Number ?? int.MaxValue, p.ExternalName
                select p)
            .ToListAsync(ct);

        var prIds = pullRequests.Select(p => p.Id).ToList();

        var commitsByPr = prIds.Count == 0
            ? []
            : await (
                    from j in db.PullRequestCommits.AsNoTracking()
                    join c in db.Commits.AsNoTracking() on j.CommitId equals c.Id
                    where prIds.Contains(j.PullRequestId)
                    orderby j.PullRequestId, c.AuthoredAt, c.Sha
                    select new { j.PullRequestId, Commit = c })
                .ToListAsync(ct);

        var commitIds = commitsByPr.Select(x => x.Commit.Id).Distinct().ToList();

        var runsByCommit = commitIds.Count == 0
            ? []
            : await db.WorkflowRuns.AsNoTracking()
                .Where(r => r.CommitId != null && commitIds.Contains(r.CommitId.Value))
                .OrderBy(r => r.RunNumber ?? long.MaxValue)
                .ToListAsync(ct);

        var runIds = runsByCommit.Select(r => r.Id).ToList();

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

        var deployments = artifactIds.Count == 0
            ? []
            : await db.Deployments.AsNoTracking()
                .Include(d => d.Service)
                .Include(d => d.Environment)
                .Where(d => artifactIds.Contains(d.ArtifactId))
                .OrderByDescending(d => d.DeployedAt)
                .ThenByDescending(d => d.IngestedSequence)
                .ToListAsync(ct);

        var evidence = await EvidenceLoader.LoadAsync(db,
            workItemIds: [workItem.Id],
            pullRequestIds: prIds,
            commitIds: commitIds,
            workflowRunIds: runIds,
            buildArtifactIds: artifactIds,
            deploymentIds: deployments.Select(d => d.Id).ToList(),
            ct);

        var chains = pullRequests.Select(pr => new DeploymentChain
        {
            PullRequest = ResultMappers.ToNode(pr, evidence),
            Commits = commitsByPr
                .Where(x => x.PullRequestId == pr.Id)
                .Select(x => x.Commit)
                .DistinctBy(c => c.Id)
                .Select(commit => new CommitChain
                {
                    Commit = ResultMappers.ToNode(commit, evidence),
                    WorkflowRuns = runsByCommit
                        .Where(r => r.CommitId == commit.Id)
                        .Select(run => new WorkflowRunChain
                        {
                            WorkflowRun = ResultMappers.ToNode(run, evidence),
                            Artifacts = artifactsByRun
                                .Where(x => x.WorkflowRunId == run.Id)
                                .Select(x => x.Artifact)
                                .DistinctBy(a => a.Id)
                                .Select(artifact => new ArtifactChain
                                {
                                    Artifact = ResultMappers.ToNode(artifact, evidence),
                                    Deployments = deployments
                                        .Where(d => d.ArtifactId == artifact.Id
                                            && (d.WorkflowRunId is null || d.WorkflowRunId == run.Id))
                                        .Select(d => ResultMappers.ToNode(d, evidence))
                                        .ToList(),
                                })
                                .ToList(),
                        })
                        .ToList(),
                })
                .ToList(),
        }).ToList();

        return new WorkItemDeploymentResult
        {
            WorkItem = ResultMappers.ToNode(workItem, evidence),
            Chains = chains,
            GeneratedAt = DateTimeOffset.UtcNow,
        };
    }
}
