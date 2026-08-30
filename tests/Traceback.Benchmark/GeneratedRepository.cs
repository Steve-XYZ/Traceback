using Traceback.Tests.GitHubSupport;

namespace Traceback.Benchmark;

/// <summary>
/// Builds a GitHub-shaped repository large enough to expose per-item API and
/// database behaviour: pull requests with their own commit listings, a default
/// branch history, workflow runs (a slice of them rerun), and artifacts on a
/// minority of runs. Fully deterministic so runs are comparable.
/// </summary>
internal static class GeneratedRepository
{
    public const string Owner = "acme";
    public const string Name = "player-manager";

    public static FakeGitHubRepository Build(BenchmarkScale scale, DateTimeOffset now)
    {
        var world = new FakeGitHubRepository { Owner = Owner, Name = Name };
        var random = new Random(20260825);

        // Spread activity across the lookback window so incremental filters and
        // watermarks behave as they would against a live repository.
        var window = TimeSpan.FromDays(scale.LookbackDays - 1);
        DateTimeOffset At(double fraction) => now - window + (window * fraction);

        for (var i = 0; i < scale.PullRequests; i++)
        {
            var number = i + 1;
            var opened = At((double)i / scale.PullRequests);
            var commits = new List<FakeCommit>();
            for (var c = 0; c < scale.CommitsPerPullRequest; c++)
            {
                commits.Add(new FakeCommit
                {
                    Sha = Sha($"pr{number}-c{c}"),
                    Message = $"PR {number} step {c}",
                    AuthorDate = opened.AddMinutes(c * 7),
                    CommitterDate = opened.AddMinutes(c * 7),
                    AuthorName = $"dev{i % 25}",
                    AuthorEmail = $"dev{i % 25}@example.com",
                    AuthorLogin = $"dev{i % 25}",
                    CommitterName = $"dev{i % 25}",
                    CommitterEmail = $"dev{i % 25}@example.com",
                    CommitterLogin = $"dev{i % 25}",
                });
            }

            var merged = i % 3 != 0;
            world.AddPullRequest(
                new FakePullRequest
                {
                    Number = number,
                    Title = $"Change {number}",
                    State = merged ? "closed" : "open",
                    CreatedAt = opened,
                    UpdatedAt = opened.AddHours(2),
                    MergedAt = merged ? opened.AddHours(2) : null,
                    MergeCommitSha = merged ? Sha($"merge-{number}") : null,
                    HeadSha = commits[^1].Sha,
                    HeadRef = $"feature/change-{number}",
                    UserLogin = $"dev{i % 25}",
                },
                commits);
        }

        // Default-branch history beyond the commits carried by pull requests.
        for (var i = 0; i < scale.StandaloneCommits; i++)
        {
            var at = At((double)i / scale.StandaloneCommits);
            world.Commits.Add(new FakeCommit
            {
                Sha = Sha($"main-{i}"),
                Message = $"main commit {i}",
                AuthorDate = at,
                CommitterDate = at,
                AuthorName = $"dev{i % 25}",
                AuthorEmail = $"dev{i % 25}@example.com",
                AuthorLogin = $"dev{i % 25}",
                CommitterName = $"dev{i % 25}",
                CommitterEmail = $"dev{i % 25}@example.com",
                CommitterLogin = $"dev{i % 25}",
            });
        }

        var allShas = world.Commits.Select(c => c.Sha).ToList();
        for (var i = 0; i < scale.WorkflowRuns; i++)
        {
            var runId = 100_000L + i;
            var createdAt = At((double)i / scale.WorkflowRuns);
            var sha = allShas[random.Next(allShas.Count)];
            var conclusion = i % 11 == 0 ? "failure" : i % 17 == 0 ? "cancelled" : "success";

            // Every eleventh failure is retried; both attempts stay addressable.
            var rerun = conclusion == "failure" && i % 22 == 0;
            var attempt1 = new FakeRun
            {
                Id = runId,
                Name = i % 2 == 0 ? "build" : "test",
                Path = i % 2 == 0 ? ".github/workflows/build.yml" : ".github/workflows/test.yml",
                RunNumber = i + 1,
                RunAttempt = 1,
                HeadSha = sha,
                Status = "completed",
                Conclusion = conclusion,
                CreatedAt = createdAt,
                RunStartedAt = createdAt,
                UpdatedAt = createdAt.AddMinutes(6),
            };

            var artifacts = i % 5 == 0
                ? new[] { new FakeArtifact { Id = 900_000L + i, Name = $"drop-{i}", CreatedAt = createdAt.AddMinutes(6) } }
                : null;

            if (!rerun)
            {
                world.AddRun(attempt1, artifacts);
                continue;
            }

            var attempt2 = new FakeRun
            {
                Id = runId,
                Name = attempt1.Name,
                Path = attempt1.Path,
                RunNumber = attempt1.RunNumber,
                RunAttempt = 2,
                HeadSha = sha,
                Status = "completed",
                Conclusion = "success",
                CreatedAt = createdAt,
                RunStartedAt = createdAt.AddMinutes(20),
                UpdatedAt = createdAt.AddMinutes(26),
            };
            world.AddRunAttempt(attempt1);
            world.AddRunAttempt(attempt2);
            world.AddRun(attempt2, artifacts);
        }

        return world;
    }

    /// <summary>
    /// Deterministic 40-character hexadecimal object name. Git names objects
    /// with SHA-1, but nothing here verifies content, so a truncated SHA-256
    /// produces the same shape without using a weak algorithm.
    /// </summary>
    private static string Sha(string seed)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexStringLower(bytes)[..40];
    }
}

internal sealed record BenchmarkScale(
    int PullRequests,
    int CommitsPerPullRequest,
    int StandaloneCommits,
    int WorkflowRuns,
    int LookbackDays)
{
    public static readonly BenchmarkScale Default = new(
        PullRequests: 500,
        CommitsPerPullRequest: 4,
        StandaloneCommits: 3000,
        WorkflowRuns: 3000,
        LookbackDays: 90);

    /// <summary>A tenth of Default, for checking how a cost scales.</summary>
    public static readonly BenchmarkScale Small = new(
        PullRequests: 50,
        CommitsPerPullRequest: 4,
        StandaloneCommits: 300,
        WorkflowRuns: 300,
        LookbackDays: 90);
}
