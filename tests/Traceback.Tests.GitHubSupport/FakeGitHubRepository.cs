using System.Text;
using System.Text.Json;

namespace Traceback.Tests.GitHubSupport;

/// <summary>An in-memory pull request in the fake GitHub world.</summary>
public sealed class FakePullRequest
{
    public required int Number { get; init; }
    public string Title { get; set; } = "";
    /// <summary>"open" | "closed". Merged state derives from MergedAt.</summary>
    public string State { get; set; } = "open";
    public bool Draft { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public string? MergeCommitSha { get; set; }
    public string HeadRef { get; set; } = "feature";
    public required string HeadSha { get; init; }
    public string BaseRef { get; set; } = "main";
    public string UserLogin { get; set; } = "octocat";
}

/// <summary>An in-memory commit.</summary>
public sealed class FakeCommit
{
    public required string Sha { get; init; }
    public string Message { get; set; } = "";
    public DateTimeOffset AuthorDate { get; set; }
    public DateTimeOffset CommitterDate { get; set; }
    public string AuthorName { get; set; } = "Octo Cat";
    public string AuthorEmail { get; set; } = "octocat@example.com";
    public string? AuthorLogin { get; set; } = "octocat";
    public string CommitterName { get; set; } = "Octo Cat";
    public string CommitterEmail { get; set; } = "octocat@example.com";
    public string? CommitterLogin { get; set; } = "octocat";
}

public sealed class FakeRun
{
    public required long Id { get; init; }
    public string Name { get; set; } = "ci";
    public long WorkflowId { get; set; } = 100;
    public string Path { get; set; } = ".github/workflows/ci.yml";
    public long RunNumber { get; set; } = 1;
    public int RunAttempt { get; set; } = 1;
    public string Event { get; set; } = "push";
    public string Status { get; set; } = "completed";
    public string? Conclusion { get; set; } = "success";
    public string? HeadBranch { get; set; } = "main";
    public required string HeadSha { get; init; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? RunStartedAt { get; set; }
}

public sealed class FakeArtifact
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public long SizeInBytes { get; set; } = 1024;
    public bool Expired { get; set; }
    public string? Digest { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// The complete state of a fake GitHub repository: pull requests with their
/// commit memberships, default-branch commits, workflow runs with attempts,
/// and run artifacts. Tests mutate this world between synchronization passes
/// to simulate provider activity.
/// </summary>
public sealed class FakeGitHubRepository
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public string FullName => $"{Owner}/{Name}";
    public string Description { get; set; } = "A fake repository";
    public bool Private { get; set; } = true;
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Repository-level timestamps served by the fake's /repos endpoint. Fixed
    /// defaults keep an unchanged repository fingerprinting identically across
    /// synchronization passes; tests mutate them to simulate repo activity.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; } = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset PushedAt { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Newest first (as the updated-sorted list API returns).</summary>
    public List<FakePullRequest> PullRequests { get; } = [];
    public Dictionary<int, List<FakeCommit>> PullRequestCommits { get; } = [];

    /// <summary>Default-branch commits as the commits list API returns them.</summary>
    public List<FakeCommit> Commits { get; } = [];

    public List<FakeRun> Runs { get; } = [];
    /// <summary>Rerun attempts per run id; the entry in Runs reflects the latest attempt.</summary>
    public Dictionary<long, List<FakeRun>> RunAttempts { get; } = [];
    public Dictionary<long, List<FakeArtifact>> Artifacts { get; } = [];

    public FakePullRequest AddPullRequest(FakePullRequest pr, IEnumerable<FakeCommit>? commits = null)
    {
        PullRequests.Add(pr);
        if (commits is not null)
        {
            foreach (var c in commits)
                Commits.Add(c);
            PullRequestCommits[pr.Number] = [.. commits];
        }
        return pr;
    }

    public FakeRun AddRun(FakeRun run, IEnumerable<FakeArtifact>? artifacts = null)
    {
        Runs.Add(run);
        if (artifacts is not null)
            Artifacts[run.Id] = [.. artifacts];
        return run;
    }

    /// <summary>
    /// Registers a rerun attempt for enumeration by the attempts endpoint.
    /// The runs listing itself only exposes the latest attempt per run id,
    /// exactly like GitHub.
    /// </summary>
    public void AddRunAttempt(FakeRun attemptRun)
    {
        var attempts = RunAttempts.TryGetValue(attemptRun.Id, out var list)
            ? list
            : RunAttempts[attemptRun.Id] = [];
        attempts.Add(attemptRun);
    }
}
