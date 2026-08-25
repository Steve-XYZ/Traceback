namespace Traceback.Domain.Entities;

/// <summary>WorkItem IMPLEMENTED_BY PullRequest. Many-to-many.</summary>
public sealed class WorkItemPullRequest
{
    public Guid WorkItemId { get; set; }
    public WorkItem WorkItem { get; set; } = null!;

    public Guid PullRequestId { get; set; }
    public PullRequest PullRequest { get; set; } = null!;

    /// <summary>Ingestion sequence of the observation that first established the edge.</summary>
    public long EstablishedSequence { get; set; }
}

/// <summary>PullRequest CONTAINS Commit. Many-to-many.</summary>
public sealed class PullRequestCommit
{
    public Guid PullRequestId { get; set; }
    public PullRequest PullRequest { get; set; } = null!;

    public Guid CommitId { get; set; }
    public Commit Commit { get; set; } = null!;

    public long EstablishedSequence { get; set; }
}

/// <summary>WorkflowRun PRODUCES BuildArtifact. Many-to-many (matrix builds, re-runs).</summary>
public sealed class WorkflowRunArtifact
{
    public Guid WorkflowRunId { get; set; }
    public WorkflowRun WorkflowRun { get; set; } = null!;

    public Guid BuildArtifactId { get; set; }
    public BuildArtifact BuildArtifact { get; set; } = null!;

    public long EstablishedSequence { get; set; }
}
