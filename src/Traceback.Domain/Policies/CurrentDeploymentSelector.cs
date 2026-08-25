using Traceback.Domain.Entities;

namespace Traceback.Domain.Policies;

/// <summary>
/// Derives the currently running revision of a service in an environment from
/// deployment history. Pure function: the "current version" is never stored as a
/// mutable pointer; it is always derived so that history and current state can
/// never disagree.
/// </summary>
public static class CurrentDeploymentSelector
{
    /// <summary>
    /// Returns the deployment to present as "currently running": the most recent
    /// explicitly successful deployment by deployed time, with ingestion sequence
    /// as deterministic tie-breaker for identical timestamps. Returns null when no
    /// successful deployment is known.
    /// </summary>
    public static Deployment? Select(IEnumerable<Deployment> deployments)
    {
        return deployments
            .Where(d => d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.DeployedAt)
            .ThenByDescending(d => d.IngestedSequence)
            .FirstOrDefault();
    }
}
