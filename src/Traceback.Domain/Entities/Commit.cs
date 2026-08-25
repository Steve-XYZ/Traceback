namespace Traceback.Domain.Entities;

public sealed class Commit : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Full commit SHA, lowercased.</summary>
    public string Sha { get; set; } = null!;

    /// <summary>
    /// Repository context of this commit object. Git object identity is content
    /// addressing and a SHA can technically appear in several repositories
    /// (forks); Traceback keeps per-repository rows so repository-specific
    /// relationships never leak across repositories.
    /// </summary>
    public Guid? SourceRepositoryId { get; set; }
    public SourceRepository? SourceRepository { get; set; }

    /// <summary>Display form of the owning repository.</summary>
    public string? Repository { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? AuthoredAt { get; set; }
    public DateTimeOffset? CommittedAt { get; set; }

    public Guid? AuthorEngineerId { get; set; }
    public Engineer? Author { get; set; }

    public Guid? CommitterEngineerId { get; set; }
    public Engineer? Committer { get; set; }

    public List<PullRequestCommit> InPullRequests { get; set; } = [];
    public List<WorkflowRun> BuiltBy { get; set; } = [];
}
