namespace Traceback.Application.Queries;

/// <summary>
/// Points at the exact observation that established a relationship between two
/// entities, making every reported relationship reconstructible from evidence.
/// </summary>
public sealed record RelationshipEvidence(
    long ObservationSequence,
    string Provider,
    string EntityType,
    string ExternalKey,
    DateTimeOffset OccurredAt,
    DateTimeOffset ObservedAt);

/// <summary>Discovery-level view of a synchronized source repository.</summary>
public sealed record SourceRepositorySummary(
    string Provider,
    string Key,
    string FullName,
    string? Description,
    string? Visibility,
    string? DefaultBranch,
    string? Url);

/// <summary>Pull request context: PR → commits → workflow runs → artifacts.</summary>
public sealed record PullRequestContextResult
{
    public required string RepositoryKey { get; init; }
    public required int Number { get; init; }
    public required PullRequestNode PullRequest { get; init; }

    /// <summary>Commits contained in this PR in author order, each with its build activity.</summary>
    public required IReadOnlyList<CommitContext> Commits { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record CommitContext
{
    public required CommitNode Commit { get; init; }

    /// <summary>The observation that established that this PR contains this commit.</summary>
    public RelationshipEvidence? EstablishedBy { get; init; }

    public required IReadOnlyList<WorkflowRunContext> WorkflowRuns { get; init; }
}

public sealed record WorkflowRunContext
{
    public required WorkflowRunNode WorkflowRun { get; init; }
    public required IReadOnlyList<ArtifactNode> Artifacts { get; init; }
}

/// <summary>Commit delivery context: commit → PRs containing it → runs → artifacts.</summary>
public sealed record CommitDeliveryContextResult
{
    public required string RepositoryKey { get; init; }
    public required string Sha { get; init; }
    public required CommitNode Commit { get; init; }

    /// <summary>PRs observed to contain or introduce this commit (membership is provider evidence).</summary>
    public required IReadOnlyList<CommitPullRequestLink> PullRequests { get; init; }

    /// <summary>Workflow runs whose head SHA equals this commit, newest first.</summary>
    public required IReadOnlyList<WorkflowRunContext> WorkflowRuns { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record CommitPullRequestLink
{
    public required PullRequestNode PullRequest { get; init; }
    public RelationshipEvidence? EstablishedBy { get; init; }
}

/// <summary>A time-windowed page of engineering changes for one repository.</summary>
public sealed record RepositoryChangesResult
{
    public required string RepositoryKey { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required int Limit { get; init; }

    /// <summary>Entries newest first across pull requests, commits, and workflow runs.</summary>
    public required IReadOnlyList<ChangeEntry> Entries { get; init; }

    /// <summary>Opaque continuation token; null when the window is exhausted.</summary>
    public required string? NextCursor { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record ChangeEntry
{
    /// <summary>Time used to place this entry on the repository timeline.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>"pull_request" | "commit" | "workflow_run".</summary>
    public required string Kind { get; init; }

    /// <summary>Internal entity id; stabilizes pagination order between same-time entries.</summary>
    public required Guid EntityId { get; init; }
    public PullRequestChange? PullRequest { get; init; }
    public CommitChange? Commit { get; init; }
    public WorkflowRunChange? WorkflowRun { get; init; }
}

public sealed record PullRequestChange(
    string? ExternalName,
    int? Number,
    string? Title,
    string? State,
    string? Url,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? MergedAt);

public sealed record CommitChange(
    string Sha,
    string? Message,
    DateTimeOffset? AuthoredAt);

public sealed record WorkflowRunChange(
    string? ExternalName,
    long? RunId,
    int? RunAttempt,
    string? WorkflowName,
    string? Status,
    string? Conclusion,
    DateTimeOffset? StartedAt);
