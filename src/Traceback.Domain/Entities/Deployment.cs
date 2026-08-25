namespace Traceback.Domain.Entities;

/// <summary>
/// The fact that a specific artifact was deployed to a service in an environment.
/// Immutable once written; corrections arrive as new observations of the same fact
/// (matched by natural key) or as new deployments.
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

    /// <summary>Workflow run that produced the artifact, when the source knows it.</summary>
    public Guid? WorkflowRunId { get; set; }
    public WorkflowRun? WorkflowRun { get; set; }

    /// <summary>Monotonic ingestion sequence, used as deterministic tie-breaker.</summary>
    public long IngestedSequence { get; set; }
}
