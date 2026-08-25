namespace Traceback.Application.Queries;

/// <summary>Provenance of a single fact: which provider, which external object, when.</summary>
public sealed record SourceEvidence(
    string Provider,
    string ExternalKey,
    string? ExternalUrl,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset LastObservedAt);

/// <summary>A node in a query result with its supporting evidence.</summary>
public abstract record EvidenceNode
{
    /// <summary>All known external identities and observations backing this node.</summary>
    public required IReadOnlyList<SourceEvidence> Sources { get; init; }
}

public sealed record WorkItemNode : EvidenceNode
{
    public required string Key { get; init; }
    public string? Title { get; init; }
    public string? Status { get; init; }
    public string? Type { get; init; }
    public string? Url { get; init; }
}

public sealed record PullRequestNode : EvidenceNode
{
    public required string ExternalName { get; init; }
    public int? Number { get; init; }
    public string? Repository { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
}

public sealed record CommitNode : EvidenceNode
{
    public required string Sha { get; init; }
    public string? Message { get; init; }
    public string? Repository { get; init; }
    public DateTimeOffset? AuthoredAt { get; init; }
}

public sealed record WorkflowRunNode : EvidenceNode
{
    public required string ExternalName { get; init; }
    public string? WorkflowName { get; init; }
    public long? RunNumber { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record ArtifactNode : EvidenceNode
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Digest { get; init; }
    public string? Uri { get; init; }
}

public sealed record DeploymentNode : EvidenceNode
{
    public required Guid Id { get; init; }
    public required DateTimeOffset DeployedAt { get; init; }
    public required string Status { get; init; }
    public required string ServiceName { get; init; }
    public required string EnvironmentName { get; init; }

    /// <summary>True when this row is a placeholder created by a reference before full observation.</summary>
    public bool IsPlaceholder { get; init; }
}

/// <summary>
/// One complete causal chain from a work item to the deployments of artifacts
/// built from its commits.
/// </summary>
public sealed record DeploymentChain
{
    public required PullRequestNode PullRequest { get; init; }
    public required IReadOnlyList<CommitChain> Commits { get; init; }
}

public sealed record CommitChain
{
    public required CommitNode Commit { get; init; }
    public required IReadOnlyList<WorkflowRunChain> WorkflowRuns { get; init; }
}

public sealed record WorkflowRunChain
{
    public required WorkflowRunNode WorkflowRun { get; init; }
    public required IReadOnlyList<ArtifactChain> Artifacts { get; init; }
}

public sealed record ArtifactChain
{
    public required ArtifactNode Artifact { get; init; }
    /// <summary>Deployments of this artifact across environments, newest first.</summary>
    public required IReadOnlyList<DeploymentNode> Deployments { get; init; }
}

public sealed record WorkItemDeploymentResult
{
    public required WorkItemNode WorkItem { get; init; }
    /// <summary>One entry per pull request implementing the work item, ordered by PR number.</summary>
    public required IReadOnlyList<DeploymentChain> Chains { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}
