namespace Traceback.Connectors.GitHub;

/// <summary>Base for GitHub API failures. Messages are sanitized and never include credentials.</summary>
public class GitHubApiException(string message) : Exception(message);

/// <summary>401/403 without a rate-limit cause. Never retried: tokens do not fix themselves.</summary>
public sealed class GitHubAuthenticationException(string message) : GitHubApiException(message);

/// <summary>404. Never retried; callers decide whether absence is acceptable.</summary>
public sealed class GitHubNotFoundException(string message) : GitHubApiException(message);

/// <summary>Primary or secondary rate limit encountered. Carries when requests may resume.</summary>
public sealed class GitHubRateLimitException(string message, DateTimeOffset? resetAt, int? retryAfterSeconds)
    : GitHubApiException(message)
{
    public DateTimeOffset? ResetAt { get; } = resetAt;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>Transient failure (network or 5xx) that persisted past the bounded retries.</summary>
public sealed class GitHubTransientException(string message) : GitHubApiException(message);

/// <summary>A page of array results plus the link-header continuation, if any.</summary>
internal sealed record GitHubArrayPage<T>(IReadOnlyList<T> Items, string? NextUrl)
{
    public bool HasNext => !string.IsNullOrEmpty(NextUrl);
}

/// <summary>A page whose payload is a wrapper object plus the link-header continuation.</summary>
internal sealed record GitHubObjectPage<T>(T Payload, string? NextUrl);

/// <summary>Artifacts page plus the repository-wide total the API reports.</summary>
internal sealed record GitHubArtifactsPage(IReadOnlyList<GitHubApiArtifact> Items, int TotalCount, string? NextUrl)
{
    public bool HasNext => !string.IsNullOrEmpty(NextUrl);
}

/// <summary>
/// Transport abstraction over the GitHub REST API: authentication, bounded
/// retries with backoff, deliberate rate-limit handling, Link-header paging,
/// and JSON deserialization to connector-internal DTOs.
/// </summary>
internal interface IGitHubApiClient
{
    Task<GitHubApiRepository> GetRepositoryAsync(string owner, string name, CancellationToken cancellationToken = default);

    Task<GitHubArrayPage<GitHubApiPullRequest>> GetPullRequestsPageAsync(
        string owner, string name, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default);

    Task<GitHubArrayPage<GitHubApiCommit>> GetPullRequestCommitsPageAsync(
        string owner, string name, int number, string? nextPageUrl, int pageSize, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default);

    Task<GitHubArrayPage<GitHubApiCommit>> GetCommitsPageAsync(
        string owner, string name, DateTimeOffset? since, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default);

    Task<GitHubArrayPage<GitHubApiWorkflowRun>> GetWorkflowRunsPageAsync(
        string owner, string name, DateTimeOffset? createdFrom, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubApiWorkflowRun>> GetRunAttemptsAsync(
        string owner, string name, long runId, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitHubApiArtifact>> GetRunArtifactsAsync(
        string owner, string name, long runId, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the repository-wide artifacts listing. Each artifact names
    /// the run that produced it, so a whole pass's artifacts can be fetched in
    /// a few requests instead of one per run.
    /// </summary>
    Task<GitHubArtifactsPage> GetRepositoryArtifactsPageAsync(
        string owner, string name, string? nextPageUrl, int pageSize, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default);
}
