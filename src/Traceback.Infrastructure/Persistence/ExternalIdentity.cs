using Traceback.Domain.Entities;

namespace Traceback.Infrastructure.Persistence;

/// <summary>Entity type discriminators used in the external identity mapping.</summary>
public static class ExternalEntityTypes
{
    public const string Engineer = "engineer";
    public const string Repository = "repository";
    public const string WorkItem = "work_item";
    public const string PullRequest = "pull_request";
    public const string Commit = "commit";
    public const string WorkflowRun = "workflow_run";
    public const string BuildArtifact = "build_artifact";
    public const string Deployment = "deployment";
    public const string Service = "service";
    public const string Environment = "environment";
    public const string ServiceInstance = "service_instance";

    /// <summary>All valid type names, used for the integrity CHECK constraint.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Engineer, Repository, WorkItem, PullRequest, Commit, WorkflowRun,
        BuildArtifact, Deployment, Service, Environment, ServiceInstance,
    ];
}

/// <summary>
/// Maps one external object (provider + external key) to an internal entity.
/// This table is the idempotency anchor of ingestion: the unique constraint on
/// (provider, entity_type, external_key) makes duplicate observations collapse
/// onto a single domain row. Exactly one typed FK column is non-null and it must
/// match EntityTypeName (enforced by a database CHECK constraint).
/// </summary>
public sealed class ExternalIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Provider { get; set; }
    public required string EntityTypeName { get; set; }
    public required string ExternalKey { get; set; }

    public Guid? EngineerId { get; set; }
    public Engineer? Engineer { get; set; }

    public Guid? SourceRepositoryId { get; set; }
    public SourceRepository? SourceRepository { get; set; }

    public Guid? WorkItemId { get; set; }
    public WorkItem? WorkItem { get; set; }

    public Guid? PullRequestId { get; set; }
    public PullRequest? PullRequest { get; set; }

    public Guid? CommitId { get; set; }
    public Commit? Commit { get; set; }

    public Guid? WorkflowRunId { get; set; }
    public WorkflowRun? WorkflowRun { get; set; }

    public Guid? BuildArtifactId { get; set; }
    public BuildArtifact? BuildArtifact { get; set; }

    public Guid? DeploymentId { get; set; }
    public Deployment? Deployment { get; set; }

    public Guid? ServiceId { get; set; }
    public Service? Service { get; set; }

    public Guid? EnvironmentId { get; set; }
    public DeploymentEnvironment? Environment { get; set; }

    public Guid? ServiceInstanceId { get; set; }
    public ServiceInstance? ServiceInstance { get; set; }

    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
}

/// <summary>
/// Append-only log of every normalized event Traceback has accepted. This is the
/// source-evidence record: what arrived, from whom, when it happened, when we saw
/// it, and exactly what was claimed. Domain rows are projections of this log.
/// </summary>
public sealed class Observation
{
    /// <summary>Ingestion sequence (monotonic, database-assigned).</summary>
    public long Sequence { get; set; }

    public required string Provider { get; init; }

    /// <summary>e.g. "WorkItemObserved".</summary>
    public required string EventType { get; init; }
    public required string EntityTypeName { get; init; }
    public required string ExternalKey { get; init; }

    /// <summary>When the fact happened according to the source system.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>When Traceback received the event.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>SHA-256 over schema version + provider + canonical event JSON. Unique.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The normalized event as received, stored as jsonb.</summary>
    public required string PayloadJson { get; init; }
}
