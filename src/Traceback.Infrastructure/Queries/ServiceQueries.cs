using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Traceback.Application.Queries;
using Traceback.Domain.Entities;
using Traceback.Domain.Policies;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Queries;

internal sealed class ServiceQueries(TracebackDbContext db) : IServiceQueries
{
    private static string Normalize(string raw) => raw.Trim().ToLowerInvariant();

    public async Task<CurrentDeploymentResult?> GetCurrentDeploymentAsync(string serviceName, string environmentName, CancellationToken cancellationToken = default)
    {
        const string queryName = "current-deployment";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        try
        {
            return await GetCurrentDeploymentCoreAsync(serviceName, environmentName, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<CurrentDeploymentResult?> GetCurrentDeploymentCoreAsync(string serviceName, string environmentName, CancellationToken ct)
    {
        var service = await db.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == Normalize(serviceName), ct);
        if (service is null)
            return null;
        var environment = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == Normalize(environmentName), ct);
        if (environment is null)
            return null;

        var deployments = await db.Deployments.AsNoTracking()
            .Include(d => d.Artifact)
            .Include(d => d.Service)
            .Include(d => d.Environment)
            .Where(d => d.ServiceId == service.Id && d.EnvironmentId == environment.Id)
            .OrderByDescending(d => d.DeployedAt)
            .ThenByDescending(d => d.IngestedSequence)
            .ToListAsync(ct);

        var current = CurrentDeploymentSelector.Select(deployments);
        if (current is null)
        {
            return new CurrentDeploymentResult
            {
                ServiceName = service.Name,
                EnvironmentName = environment.Name,
                Current = null,
                GeneratedAt = DateTimeOffset.UtcNow,
            };
        }

        // Reconstruct the source revision: artifact → producing runs → commit.
        var revision = await ResolveRevisionAsync(current, ct);

        var evidence = await EvidenceLoader.LoadAsync(db,
            buildArtifactIds: [current.Artifact.Id],
            deploymentIds: [current.Id],
            workflowRunIds: revision is null ? [] : [revision.Run.Id],
            commitIds: revision is null ? [] : [revision.Run.CommitId!.Value],
            cancellationToken: ct);

        return new CurrentDeploymentResult
        {
            ServiceName = service.Name,
            EnvironmentName = environment.Name,
            GeneratedAt = DateTimeOffset.UtcNow,
            Current = new CurrentDeploymentInfo
            {
                Deployment = ResultMappers.ToNode(current, evidence),
                Artifact = ResultMappers.ToNode(current.Artifact, evidence),
                Revision = revision is null
                    ? null
                    : ResultMappers.ToNode(revision.Run.Commit!, evidence),
            },
        };
    }

    public async Task<DeploymentHistoryResult?> GetDeploymentHistoryAsync(
        string serviceName, string environmentName, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
    {
        const string queryName = "deployment-history";
        var start = Stopwatch.GetTimestamp();
        using var activity = QueryTracing.Start(queryName);
        try
        {
            return await GetHistoryCoreAsync(serviceName, environmentName, fromUtc, toUtc, cancellationToken);
        }
        finally
        {
            QueryTracing.Record(queryName, start);
        }
    }

    private async Task<DeploymentHistoryResult?> GetHistoryCoreAsync(
        string serviceName, string environmentName, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var service = await db.Services.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == Normalize(serviceName), ct);
        if (service is null)
            return null;
        var environment = await db.Environments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == Normalize(environmentName), ct);
        if (environment is null)
            return null;

        var window = toUtc < fromUtc ? (from: toUtc, to: fromUtc) : (from: fromUtc, to: toUtc);

        var entries = await db.Deployments.AsNoTracking()
            .Include(d => d.Artifact)
            .Include(d => d.Service)
            .Include(d => d.Environment)
            .Where(d => d.ServiceId == service.Id
                        && d.EnvironmentId == environment.Id
                        && d.DeployedAt >= window.from
                        && d.DeployedAt <= window.to)
            .OrderByDescending(d => d.DeployedAt)
            .ThenByDescending(d => d.IngestedSequence)
            .ToListAsync(ct);

        var artifactIds = entries.Select(e => e.ArtifactId).Distinct().ToList();

        // Related engineering context: artifacts → runs → commits → PRs → work items.
        var runLinks = artifactIds.Count == 0
            ? []
            : await db.WorkflowRunArtifacts.AsNoTracking()
                .Where(j => artifactIds.Contains(j.BuildArtifactId))
                .ToListAsync(ct);
        var runIds = runLinks.Select(j => j.WorkflowRunId).Distinct().ToList();

        var runs = runIds.Count == 0
            ? []
            : await db.WorkflowRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id) && r.CommitId != null)
                .ToListAsync(ct);
        var commitIds = runs.Select(r => r.CommitId!.Value).Distinct().ToList();

        var commits = commitIds.Count == 0
            ? []
            : await db.Commits.AsNoTracking().Where(c => commitIds.Contains(c.Id)).ToListAsync(ct);

        var prLinks = commitIds.Count == 0
            ? []
            : await db.PullRequestCommits.AsNoTracking()
                .Where(j => commitIds.Contains(j.CommitId))
                .ToListAsync(ct);
        var prIds = prLinks.Select(j => j.PullRequestId).Distinct().ToList();

        var prs = prIds.Count == 0
            ? []
            : await db.PullRequests.AsNoTracking().Where(p => prIds.Contains(p.Id)).ToListAsync(ct);

        var wiLinks = prIds.Count == 0
            ? []
            : await db.WorkItemPullRequests.AsNoTracking()
                .Where(j => prIds.Contains(j.PullRequestId))
                .ToListAsync(ct);
        var wiIds = wiLinks.Select(j => j.WorkItemId).Distinct().ToList();

        var workItems = wiIds.Count == 0
            ? []
            : await db.WorkItems.AsNoTracking().Where(w => wiIds.Contains(w.Id)).ToListAsync(ct);

        var evidence = await EvidenceLoader.LoadAsync(db,
            workItemIds: wiIds,
            pullRequestIds: prIds,
            commitIds: commitIds,
            workflowRunIds: runIds,
            buildArtifactIds: artifactIds,
            deploymentIds: entries.Select(e => e.Id).ToList(),
            cancellationToken: ct);

        var historyEntries = entries.Select(deployment =>
        {
            var relatedRuns = runLinks
                .Where(j => j.BuildArtifactId == deployment.ArtifactId)
                .Select(j => j.WorkflowRunId)
                .ToHashSet();
            var relatedCommits = runs.Where(r => relatedRuns.Contains(r.Id))
                .Select(r => r.CommitId!.Value)
                .ToHashSet();
            var relatedPrs = prLinks.Where(j => relatedCommits.Contains(j.CommitId))
                .Select(j => j.PullRequestId)
                .ToHashSet();
            var relatedWIs = wiLinks.Where(j => relatedPrs.Contains(j.PullRequestId))
                .Select(j => j.WorkItemId)
                .ToHashSet();

            return new DeploymentHistoryEntry
            {
                Deployment = ResultMappers.ToNode(deployment, evidence),
                Artifact = ResultMappers.ToNode(deployment.Artifact, evidence),
                Commits = commits.Where(c => relatedCommits.Contains(c.Id))
                    .OrderBy(c => c.AuthoredAt)
                    .ThenBy(c => c.Sha)
                    .Select(c => ResultMappers.ToNode(c, evidence))
                    .ToList(),
                PullRequests = prs.Where(p => relatedPrs.Contains(p.Id))
                    .OrderBy(p => p.Number ?? int.MaxValue)
                    .Select(p => ResultMappers.ToNode(p, evidence))
                    .ToList(),
                WorkItems = workItems.Where(w => relatedWIs.Contains(w.Id))
                    .OrderBy(w => w.Key)
                    .Select(w => ResultMappers.ToNode(w, evidence))
                    .ToList(),
            };
        }).ToList();

        return new DeploymentHistoryResult
        {
            ServiceName = service.Name,
            EnvironmentName = environment.Name,
            From = window.from,
            To = window.to,
            Deployments = historyEntries,
        };
    }

    private sealed record RevisionResolution(WorkflowRun Run);

    /// <summary>
    /// Picks the most recent completed run that produced the artifact and whose
    /// commit is known. Returns null when the chain cannot be reconstructed yet
    /// (e.g., deployment observed before its build pipeline).
    /// </summary>
    private async Task<RevisionResolution?> ResolveRevisionAsync(Deployment current, CancellationToken ct)
    {
        // Include is not honored through joins/projections, so resolve the run id
        // first and then load the run with its commit.
        var runId = await (
                from j in db.WorkflowRunArtifacts.AsNoTracking()
                join r in db.WorkflowRuns.AsNoTracking() on j.WorkflowRunId equals r.Id
                where j.BuildArtifactId == current.ArtifactId && r.CommitId != null
                orderby r.CompletedAt descending, r.StartedAt descending, r.RunNumber descending
                select r.Id)
            .FirstOrDefaultAsync(ct);
        if (runId == Guid.Empty)
            return null;

        var run = await db.WorkflowRuns.AsNoTracking()
            .Include(r => r.Commit)
            .FirstAsync(r => r.Id == runId, ct);
        return new RevisionResolution(run);
    }
}
