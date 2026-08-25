using System.Diagnostics;
using Traceback.Connectors.Abstractions;

namespace Traceback.Connectors.GitHub;

/// <summary>
/// Translates GitHub API DTOs into provider-independent Traceback events.
/// Mapping rules (all provider evidence, never inference):
///
/// - PR state: merged_at present → "merged"; else state "closed" → "closed";
///   else "open".
/// - PR ↔ commit membership comes from the pull request's commits listing
///   (plus the head SHA stated by the PR object itself).
/// - Workflow run identity is (repository, run id, attempt): each rerun
///   attempt becomes a distinct event and a distinct historical row.
/// - Run completion: updated_at is only projected as CompletedAt when the run
///   status is "completed".
/// - Actions artifacts map to BuildArtifact with a provider-stable canonical
///   key hint; GitHub exposes no REST evidence linking a run to a container
///   image, so no image relationship is ever fabricated here.
/// </summary>
internal sealed class GitHubEventMapper(string owner, string name)
{
    public string RepositoryKey { get; } = $"{owner}/{name}".ToLowerInvariant();

    private int _sequence;

    public RepositoryObserved MapRepository(GitHubApiRepository repo)
    {
        var occurred = repo.UpdatedAt ?? repo.PushedAt ?? DateTimeOffset.UtcNow;
        return new RepositoryObserved(
            Provenance(ExternalEntityTypes.Repository, RepositoryKey, null, occurred),
            RepositoryKey,
            FullName: repo.FullName ?? RepositoryKey,
            Owner: repo.Owner?.Login ?? owner,
            Name: repo.Name ?? name,
            Description: repo.Description,
            Visibility: repo.Private ? "private" : "public",
            DefaultBranch: repo.DefaultBranch,
            Url: repo.HtmlUrl);
    }

    public PullRequestObserved MapPullRequest(GitHubApiPullRequest pr, IReadOnlyList<string> commitShas)
    {
        var externalName = $"{RepositoryKey}#{pr.Number}";
        var state = pr.MergedAt is not null || pr.Merged ? "merged" : pr.State == "closed" ? "closed" : pr.Draft ? "draft" : "open";
        var shas = commitShas
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet();
        if (!string.IsNullOrWhiteSpace(pr.Head?.Sha))
            shas.Add(pr.Head.Sha.Trim().ToLowerInvariant());

        return new PullRequestObserved(
            Provenance(ExternalEntityTypes.PullRequest, externalName, pr.HtmlUrl, pr.UpdatedAt ?? pr.CreatedAt ?? DateTimeOffset.UtcNow),
            externalName,
            Repository: RepositoryKey,
            Number: pr.Number,
            Title: pr.Title,
            State: state,
            Url: pr.HtmlUrl,
            MergedAt: pr.MergedAt,
            Author: pr.User?.Login is { } login ? new EngineerRef(login, null) : null,
            CommitShas: [.. shas],
            CreatedAt: pr.CreatedAt,
            UpdatedAt: pr.UpdatedAt,
            ClosedAt: pr.ClosedAt,
            MergeCommitSha: pr.MergeCommitSha,
            HeadSha: pr.Head?.Sha,
            HeadBranch: pr.Head?.Ref,
            BaseBranch: pr.Base?.Ref);
    }

    public CommitObserved MapCommit(GitHubApiCommit commit)
    {
        var sha = NormalizeSha(commit.Sha);
        var details = commit.Details;
        // Identity keys embed the repository scope so identical SHAs in
        // different repositories stay independent.
        var identityKey = $"{RepositoryKey}@{sha}";
        return new CommitObserved(
            Provenance(ExternalEntityTypes.Commit, identityKey, commit.HtmlUrl, details?.Committer?.Date ?? details?.Author?.Date ?? DateTimeOffset.UtcNow),
            sha,
            Repository: RepositoryKey,
            Message: details?.Message,
            AuthoredAt: details?.Author?.Date,
            Author: Person(details?.Author, commit.Author),
            CommittedAt: details?.Committer?.Date,
            Committer: Person(details?.Committer, commit.Committer));
    }

    public WorkflowRunObserved MapWorkflowRun(
        GitHubApiWorkflowRun run,
        IReadOnlyList<ArtifactDescriptor> artifacts)
    {
        var attempt = Math.Max(1, run.RunAttempt);
        var externalName = $"{RepositoryKey}/actions/runs/{run.Id}/attempts/{attempt}";
        var started = run.RunStartedAt ?? run.CreatedAt;
        var completed = run.Status == "completed" ? run.UpdatedAt : null;
        return new WorkflowRunObserved(
            Provenance(ExternalEntityTypes.WorkflowRun, externalName.ToLowerInvariant(), run.HtmlUrl, run.UpdatedAt ?? run.CreatedAt ?? DateTimeOffset.UtcNow),
            externalName.ToLowerInvariant(),
            WorkflowName: run.Name ?? run.Path,
            RunNumber: run.RunNumber,
            Status: run.Status,
            Conclusion: run.Conclusion,
            StartedAt: started,
            CompletedAt: completed,
            CommitSha: NormalizeSha(run.HeadSha),
            ProducedArtifacts: artifacts,
            Repository: RepositoryKey,
            RunId: run.Id,
            RunAttempt: attempt,
            Branch: run.HeadBranch,
            TriggerEvent: run.Event,
            Url: run.HtmlUrl,
            UpdatedAt: run.UpdatedAt);
    }

    public ArtifactDescriptor MapArtifact(GitHubApiArtifact artifact) =>
        new(
            Name: artifact.Name ?? $"artifact-{artifact.Id}",
            Version: null,
            Digest: null,
            Uri: artifact.ArchiveDownloadUrl,
            CanonicalKeyHint: $"{RepositoryKey}/actions/artifacts/{artifact.Id}");

    internal static string NormalizeSha(string? sha) => (sha ?? string.Empty).Trim().ToLowerInvariant();

    private static EngineerRef? Person(GitHubApiGitPerson? gitPerson, GitHubApiUser? user) =>
        gitPerson is null && user?.Login is null
            ? null
            : new EngineerRef(gitPerson?.Name ?? user?.Login, gitPerson?.Email);

    private EventProvenance Provenance(string entityType, string externalKey, string? url, DateTimeOffset occurredAt) =>
        new("github", entityType, externalKey.ToLowerInvariant(), url, occurredAt, NextObservedAt());

    /// <summary>Monotonic receive timestamps within one mapping pass.</summary>
    private DateTimeOffset NextObservedAt() =>
        DateTimeOffset.UtcNow.AddMilliseconds(Interlocked.Increment(ref _sequence));
}

internal static class ExternalEntityTypes
{
    public const string Repository = "repository";
    public const string PullRequest = "pull_request";
    public const string Commit = "commit";
    public const string WorkflowRun = "workflow_run";
}
