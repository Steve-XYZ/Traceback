using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// REST list pagination end to end: streams whose listings span several pages
/// are fully ingested in one pass, the configured page size reaches the
/// provider's requests, and the per-pass page cap truncates a walk without
/// advancing its checkpoint, so the next pass redoes the window idempotently
/// instead of silently skipping data behind the cap.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubPaginationTests(PostgresContainerFixture postgres)
{
    private const int PageSize = 2;

    [Fact]
    public async Task Pull_requests_spanning_multiple_pages_are_fully_ingested()
    {
        var world = GitHubSyncHarness.NewWorld();
        for (var i = 1; i <= 5; i++)
            SeedPullRequest(world, number: i, updatedAt: TestTimes.Old.AddMinutes(i));

        var (app, handler) = await StartWithAsync(world);
        await using var _ = app;

        var result = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // Five PRs at two per page: exactly three listing pages.
        Assert.Equal(3, handler.RequestLog.Count(p => p.Contains("/pulls?", StringComparison.Ordinal)));
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(app, "pull_requests"));
        // Each PR contributes its head/membership commit.
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(app, "commits"));
        // 1 repository + 5 pull requests + 5 commits.
        Assert.Equal(11, result.TotalObservationsApplied);
    }

    [Fact]
    public async Task Commits_and_workflow_runs_spanning_multiple_pages_are_fully_ingested()
    {
        var world = GitHubSyncHarness.NewWorld();
        for (var i = 1; i <= 5; i++)
        {
            world.Commits.Add(new FakeCommit
            {
                Sha = $"commit{i:d2}".PadRight(40, 'c'),
                AuthorDate = TestTimes.Older.AddMinutes(i),
                CommitterDate = TestTimes.Older.AddMinutes(i),
            });
        }
        for (var i = 1; i <= 3; i++)
        {
            var run = new FakeRun
            {
                Id = 900 + i,
                HeadSha = $"commit{i:d2}".PadRight(40, 'c'),
                CreatedAt = TestTimes.Old,
                UpdatedAt = TestTimes.Old,
                RunStartedAt = TestTimes.Old,
            };
            world.AddRun(run, i == 1 ? [new FakeArtifact { Id = 500, Name = "drop" }] : null);
        }

        var (app, handler) = await StartWithAsync(world);
        await using var _ = app;

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // Five commits and three runs at two per page.
        Assert.Equal(3, handler.RequestLog.Count(p => p.Contains("/commits?", StringComparison.Ordinal)));
        Assert.Equal(2, handler.RequestLog.Count(p => p.Contains("/actions/runs?", StringComparison.Ordinal)));
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(app, "commits"));
        Assert.Equal(3, await GitHubSyncHarness.CountRowsAsync(app, "workflow_runs"));
        Assert.Equal(1, await GitHubSyncHarness.CountRowsAsync(app, "build_artifacts"));
    }

    [Fact]
    public async Task Configured_page_size_reaches_every_listing_request()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 42, updatedAt: TestTimes.Old);

        var (app, handler) = await StartWithAsync(world);
        await using var _ = app;

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Contains(handler.RequestLog, p => p.Contains("pulls?") && p.Contains($"per_page={PageSize}"));
        Assert.Contains(handler.RequestLog, p => p.Contains("commits?") && p.Contains($"per_page={PageSize}"));
        Assert.Contains(handler.RequestLog, p => p.Contains("actions/runs?") && p.Contains($"per_page={PageSize}"));
    }

    [Fact]
    public async Task Page_cap_truncates_without_advancing_the_checkpoint_and_the_next_pass_completes()
    {
        var world = GitHubSyncHarness.NewWorld();
        for (var i = 1; i <= 5; i++)
            SeedPullRequest(world, number: i, updatedAt: TestTimes.Old.AddMinutes(i));

        var capped = new Dictionary<string, string?>(GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"))
        {
            ["GitHub:MaxPagesPerFetch"] = $"{PageSize}",
        };

        await using var appA = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: capped);

        var firstPass = await GitHubSyncHarness.SyncAsync(appA);

        // The cap is a safety valve, not an error: what was fetched is durable...
        Assert.True(firstPass.Success, FailureMessage(firstPass));
        Assert.Equal(4, await GitHubSyncHarness.CountRowsAsync(appA, "pull_requests"));
        // ...and the checkpoint did not advance past truncated data.
        var prOutcome = firstPass.Resources.Single(r => r.ResourceType == "pull_requests");
        Assert.False(prOutcome.CursorAdvanced);
        Assert.Equal(string.Empty, await CursorAsync(appA, "pull_requests"));

        // A later pass without the cap redoes the window and converges.
        await using var appB = await TracebackApp.RestartAgainstSameDatabaseAsync(
            postgres.Container,
            appA.DatabaseName,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"));

        var secondPass = AssertSynced(await GitHubSyncHarness.SyncAsync(appB));

        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(appB, "pull_requests"));
        Assert.True(secondPass.TotalDuplicates > 0);
        Assert.NotEqual(string.Empty, await CursorAsync(appB, "pull_requests"));
    }

    private async Task<(TracebackApp App, FakeGitHubApiHandler Handler)> StartWithAsync(FakeGitHubRepository world)
    {
        var handler = new FakeGitHubApiHandler { Repository = world };
        var app = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(handler),
            settings: GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"));
        return (app, handler);
    }

    private static FakeGitHubRepository SeedPullRequest(FakeGitHubRepository world, int number, DateTimeOffset updatedAt)
    {
        var sha = $"sha{number:d4}".PadRight(40, 'b');
        world.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR #{number}",
                CreatedAt = updatedAt.AddHours(-1),
                UpdatedAt = updatedAt,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = TestTimes.Older, CommitterDate = TestTimes.Older }]);
        return world;
    }

    private static async Task<string> CursorAsync(TracebackApp app, string resourceType)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT cursor FROM sync_states WHERE integration_id = $1 AND resource_type = $2",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", resourceType);
        return rows.FirstOrDefault() ?? string.Empty;
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success, FailureMessage(result));
        return result;
    }

    private static string FailureMessage(RepositorySyncResult result) =>
        $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
        string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]";
}
