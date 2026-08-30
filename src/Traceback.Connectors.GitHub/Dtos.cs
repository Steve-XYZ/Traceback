using System.Text.Json.Serialization;

namespace Traceback.Connectors.GitHub;

// GitHub REST v3 DTOs. These types never leave the connector assembly; the
// event mapper translates them into provider-independent Traceback events.

internal sealed class GitHubApiUser
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }
}

internal sealed class GitHubApiRepositoryRef
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

internal sealed class GitHubApiGitPerson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; set; }
}

internal sealed class GitHubApiRepository
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("owner")]
    public GitHubApiUser? Owner { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("private")]
    public bool Private { get; set; }

    [JsonPropertyName("default_branch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("pushed_at")]
    public DateTimeOffset? PushedAt { get; set; }
}

internal sealed class GitHubApiPullRequest
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>"open" | "closed".</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("merged_at")]
    public DateTimeOffset? MergedAt { get; set; }

    [JsonPropertyName("closed_at")]
    public DateTimeOffset? ClosedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("merge_commit_sha")]
    public string? MergeCommitSha { get; set; }

    [JsonPropertyName("user")]
    public GitHubApiUser? User { get; set; }

    [JsonPropertyName("head")]
    public GitHubApiPullRequestSide? Head { get; set; }

    [JsonPropertyName("base")]
    public GitHubApiPullRequestSide? Base { get; set; }
}

internal sealed class GitHubApiPullRequestSide
{
    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("repo")]
    public GitHubApiRepositoryRef? Repo { get; set; }
}

internal sealed class GitHubApiCommit
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("commit")]
    public GitHubApiCommitDetails? Details { get; set; }

    [JsonPropertyName("author")]
    public GitHubApiUser? Author { get; set; }

    [JsonPropertyName("committer")]
    public GitHubApiUser? Committer { get; set; }

    [JsonPropertyName("parents")]
    public List<GitHubApiCommitParent>? Parents { get; set; }
}

internal sealed class GitHubApiCommitParent
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }
}

internal sealed class GitHubApiCommitDetails
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("author")]
    public GitHubApiGitPerson? Author { get; set; }

    [JsonPropertyName("committer")]
    public GitHubApiGitPerson? Committer { get; set; }
}

internal sealed class GitHubApiWorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Workflow display name at run time.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("workflow_id")]
    public long WorkflowId { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("run_number")]
    public long RunNumber { get; set; }

    [JsonPropertyName("run_attempt")]
    public int RunAttempt { get; set; } = 1;

    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>queued | in_progress | completed.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>success | failure | cancelled | skipped | neutral | timed_out | ...</summary>
    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }

    [JsonPropertyName("head_branch")]
    public string? HeadBranch { get; set; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("run_started_at")]
    public DateTimeOffset? RunStartedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

internal sealed class GitHubApiWorkflowRunsPage
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("workflow_runs")]
    public List<GitHubApiWorkflowRun>? WorkflowRuns { get; set; }
}

internal sealed class GitHubApiArtifact
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size_in_bytes")]
    public long SizeInBytes { get; set; }

    [JsonPropertyName("archive_download_url")]
    public string? ArchiveDownloadUrl { get; set; }

    [JsonPropertyName("expired")]
    public bool Expired { get; set; }

    /// <summary>GitHub's SHA-256 digest for the archived artifact.</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    /// <summary>
    /// Present on the repository-level artifacts listing, absent on the
    /// per-run listing (where the run is implied by the URL).
    /// </summary>
    [JsonPropertyName("workflow_run")]
    public GitHubApiArtifactRun? WorkflowRun { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

internal sealed class GitHubApiArtifactRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }
}

internal sealed class GitHubApiArtifactsPage
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("artifacts")]
    public List<GitHubApiArtifact>? Artifacts { get; set; }
}
