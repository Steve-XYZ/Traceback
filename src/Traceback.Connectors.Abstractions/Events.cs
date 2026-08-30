namespace Traceback.Connectors.Abstractions;

/// <summary>
/// Marker contract for normalized, provider-independent observation events.
/// Concrete events are records so they serialize canonically for fingerprinting.
/// </summary>
public abstract record TracebackEvent(EventProvenance Provenance);

/// <summary>
/// Events describing mutable provider state implement this to declare how fresh
/// the reported state is (the provider's own last-update time). Ingestion uses
/// it to prevent stale representations from overwriting newer state; see
/// Traceback.Domain.Policies.StateFreshnessPolicy.
/// </summary>
public interface IStateFreshness
{
    /// <summary>Provider timestamp of the state this event reports; null when unknown.</summary>
    DateTimeOffset? StateUpdatedAt { get; }
}

public sealed record RepositoryObserved(
    EventProvenance Provenance,
    // Provider-scoped identity key, e.g. "acme/player-manager".
    string Key,
    string? FullName,
    string? Owner,
    string? Name,
    string? Description,
    string? Visibility,
    string? DefaultBranch,
    string? Url) : TracebackEvent(Provenance), IStateFreshness
{
    DateTimeOffset? IStateFreshness.StateUpdatedAt => Provenance.OccurredAt;
}

public sealed record WorkItemObserved(
    EventProvenance Provenance,
    string Key,
    string? Title,
    string? Description,
    string? Status,
    string? Type,
    string? Url,
    EngineerRef? Assignee,
    IReadOnlyList<ExternalRef> ImplementsByPullRequests) : TracebackEvent(Provenance);

public sealed record PullRequestObserved(
    EventProvenance Provenance,
    string ExternalName,
    // Provider-scoped repository key owning this pull request, e.g. "acme/player-manager".
    string? Repository,
    int? Number,
    string? Title,
    string? State,
    string? Url,
    DateTimeOffset? MergedAt,
    EngineerRef? Author,
    // Commit SHAs contained in this PR (same provider as the PR).
    IReadOnlyList<string> CommitShas,
    // Provider-native lifecycle timestamps and branch/merge facts.
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? ClosedAt = null,
    string? MergeCommitSha = null,
    string? HeadSha = null,
    string? HeadBranch = null,
    string? BaseBranch = null) : TracebackEvent(Provenance), IStateFreshness
{
    DateTimeOffset? IStateFreshness.StateUpdatedAt => UpdatedAt;
}

public sealed record CommitObserved(
    EventProvenance Provenance,
    string Sha,
    string? Repository,
    string? Message,
    DateTimeOffset? AuthoredAt,
    EngineerRef? Author,
    DateTimeOffset? CommittedAt = null,
    EngineerRef? Committer = null) : TracebackEvent(Provenance);

public sealed record WorkflowRunObserved(
    EventProvenance Provenance,
    string ExternalName,
    string? WorkflowName,
    long? RunNumber,
    string? Status,
    string? Conclusion,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    // SHA of the commit this run built (same provider).
    string? CommitSha,
    IReadOnlyList<ArtifactDescriptor> ProducedArtifacts,
    // Provider-scoped repository key the run executed in, e.g. "acme/player-manager".
    string? Repository = null,
    // Provider's stable run identifier shared by all attempts of a rerun.
    long? RunId = null,
    // 1-based attempt number; each rerun attempt is a distinct historical row.
    int? RunAttempt = null,
    string? Branch = null,
    string? TriggerEvent = null,
    string? Url = null,
    DateTimeOffset? UpdatedAt = null) : TracebackEvent(Provenance), IStateFreshness
{
    DateTimeOffset? IStateFreshness.StateUpdatedAt => UpdatedAt;
}

public sealed record BuildArtifactObserved(
    EventProvenance Provenance,
    ArtifactDescriptor Artifact) : TracebackEvent(Provenance);

public sealed record DeploymentObserved(
    EventProvenance Provenance,
    string ServiceName,
    string EnvironmentName,
    ArtifactDescriptor Artifact,
    DeploymentOutcome? Outcome,
    DateTimeOffset DeployedAt,
    // Run that produced the artifact, when the deployer knows it.
    ExternalRef? TriggeredByWorkflowRun) : TracebackEvent(Provenance), IStateFreshness
{
    DateTimeOffset? IStateFreshness.StateUpdatedAt => Provenance.OccurredAt;
}

// Deployment outcome as stated by the source. Unknown maps to domain Unknown.
public sealed record DeploymentOutcome(string RawStatus)
{
    public static readonly DeploymentOutcome Succeeded = new("succeeded");
    public static readonly DeploymentOutcome Failed = new("failed");
    public static readonly DeploymentOutcome InProgress = new("in_progress");
}

public sealed record ServiceObserved(
    EventProvenance Provenance,
    string Name,
    string? Description,
    string? Team) : TracebackEvent(Provenance);

public sealed record EnvironmentObserved(
    EventProvenance Provenance,
    string Name,
    string? Kind) : TracebackEvent(Provenance);

public sealed record ServiceInstanceObserved(
    EventProvenance Provenance,
    string ExternalName,
    string ServiceName,
    string EnvironmentName,
    string? Hostname,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StoppedAt) : TracebackEvent(Provenance);

/// <summary>Lightweight person reference; connectors send whatever identity fields they have.</summary>
public sealed record EngineerRef(string? DisplayName, string? Email);
