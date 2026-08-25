namespace Traceback.Application.Queries;

public sealed record CurrentDeploymentResult
{
    public required string ServiceName { get; init; }
    public required string EnvironmentName { get; init; }

    /// <summary>Null when no successful deployment is known for this service/environment.</summary>
    public CurrentDeploymentInfo? Current { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record CurrentDeploymentInfo
{
    public required DeploymentNode Deployment { get; init; }
    public required ArtifactNode Artifact { get; init; }

    /// <summary>Source revision resolved through artifact → workflow run → commit, when reconstructable.</summary>
    public CommitNode? Revision { get; init; }
}

public sealed record DeploymentHistoryResult
{
    public required string ServiceName { get; init; }
    public required string EnvironmentName { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    /// <summary>Deployments in the window, newest first.</summary>
    public required IReadOnlyList<DeploymentHistoryEntry> Deployments { get; init; }
}

public sealed record DeploymentHistoryEntry
{
    public required DeploymentNode Deployment { get; init; }
    public required ArtifactNode Artifact { get; init; }

    /// <summary>Commits related through the producing workflow run, when known.</summary>
    public IReadOnlyList<CommitNode> Commits { get; init; } = [];
    public IReadOnlyList<PullRequestNode> PullRequests { get; init; } = [];
    public IReadOnlyList<WorkItemNode> WorkItems { get; init; } = [];
}
