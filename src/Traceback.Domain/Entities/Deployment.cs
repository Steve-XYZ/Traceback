namespace Traceback.Domain.Entities;

/// <summary>
/// The fact that a specific artifact was deployed to a service in an environment.
/// The natural key identifies one rollout, while its provider-reported lifecycle
/// status may advance as newer observations of that rollout arrive.
/// </summary>
public sealed class Deployment : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    public Guid ArtifactId { get; set; }
    public BuildArtifact Artifact { get; set; } = null!;

    public Guid ServiceId { get; set; }
    public Service Service { get; set; } = null!;

    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;

    /// <summary>When the deployment happened according to the source system (UTC).</summary>
    public DateTimeOffset DeployedAt { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Unknown;

    /// <summary>
    /// Freshest provider timestamp projected onto this deployment. Lifecycle
    /// observations use it to prevent a late older status from regressing a
    /// newer terminal state; see StateFreshnessPolicy.
    /// </summary>
    public DateTimeOffset? ProviderStateAt { get; set; }

    /// <summary>Workflow run that produced the artifact, when the source knows it.</summary>
    public Guid? WorkflowRunId { get; set; }
    public WorkflowRun? WorkflowRun { get; set; }

    /// <summary>Monotonic ingestion sequence, used as deterministic tie-breaker.</summary>
    public long IngestedSequence { get; set; }
}
