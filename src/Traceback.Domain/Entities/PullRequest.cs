namespace Traceback.Domain.Entities;

public sealed class PullRequest : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Provider-scoped display identity, e.g. "acme/player-manager#1842".</summary>
    public string ExternalName { get; set; } = null!;

    /// <summary>Repository this pull request belongs to. A PR number is only unique within a repository.</summary>
    public Guid? SourceRepositoryId { get; set; }
    public SourceRepository? SourceRepository { get; set; }

    /// <summary>Display form of the owning repository, e.g. "acme/player-manager".</summary>
    public string? Repository { get; set; }
    public int? Number { get; set; }
    public string? Title { get; set; }

    /// <summary>Provider-native state: open, merged, closed, draft, ...</summary>
    public string? State { get; set; }
    public string? Url { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? MergeCommitSha { get; set; }

    /// <summary>Tip of the source branch at last observation (provider-stated evidence).</summary>
    public string? HeadSha { get; set; }
    public string? HeadBranch { get; set; }
    public string? BaseBranch { get; set; }

    /// <summary>
    /// Freshest provider state timestamp projected onto this row (e.g. the
    /// provider's updated_at). Gates scalar overwrites so stale observations
    /// cannot clobber newer facts; see StateFreshnessPolicy.
    /// </summary>
    public DateTimeOffset? ProviderStateAt { get; set; }

    public Guid? AuthorEngineerId { get; set; }
    public Engineer? Author { get; set; }

    public List<WorkItemPullRequest> Implements { get; set; } = [];
    public List<PullRequestCommit> Contains { get; set; } = [];
}
