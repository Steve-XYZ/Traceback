using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// REST list pagination end to end: streams whose listings span several pages
/// are fully ingested in one pass, the configured page size reaches the
/// provider's requests, and the per-pass page cap reports a stream failure
/// without ingesting a partial batch or advancing its checkpoint.
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
    public async Task Repeated_page_cap_failures_do_not_report_progress_until_the_cap_is_raised()
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

        // A capped stream is an observable failure. Its partial batch is not
        // ingested and the checkpoint does not advance.
        Assert.False(firstPass.Success, FailureMessage(firstPass));
        Assert.StartsWith("pull_requests:", firstPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "pull_requests"));
        var prOutcome = firstPass.Resources.Single(r => r.ResourceType == "pull_requests");
        Assert.False(prOutcome.CursorAdvanced);
        Assert.Equal(string.Empty, await CursorAsync(appA, "pull_requests"));

        // Repeating the identical capped request remains a failure rather than
        // falsely claiming that the leading window was complete.
        var repeatedPass = await GitHubSyncHarness.SyncAsync(appA);
        Assert.False(repeatedPass.Success, FailureMessage(repeatedPass));
        Assert.StartsWith("pull_requests:", repeatedPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "pull_requests"));
        Assert.Equal(string.Empty, await CursorAsync(appA, "pull_requests"));

        // A later pass without the cap walks the full window and converges.
        await using var appB = await TracebackApp.RestartAgainstSameDatabaseAsync(
            postgres.Container,
            appA.DatabaseName,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"));

        var secondPass = AssertSynced(await GitHubSyncHarness.SyncAsync(appB));

        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(appB, "pull_requests"));
        Assert.True(secondPass.TotalDuplicates > 0); // repository metadata was redelivered.
        Assert.NotEqual(string.Empty, await CursorAsync(appB, "pull_requests"));
    }

    [Fact]
    public async Task Nested_pull_request_commit_cap_is_atomic_and_repeats_until_raised()
    {
        var world = GitHubSyncHarness.NewWorld();
        var updatedAt = TestTimes.Old;
        var commits = Enumerable.Range(1, 5)
            .Select(i => new FakeCommit
            {
                Sha = $"nested-pr-commit-{i}".PadRight(40, 'a'),
                AuthorDate = updatedAt,
                CommitterDate = updatedAt,
            })
            .ToList();
        world.AddPullRequest(
            new FakePullRequest
            {
                Number = 42,
                Title = "Nested pagination",
                CreatedAt = updatedAt.AddHours(-1),
                UpdatedAt = updatedAt,
                HeadSha = commits[^1].Sha,
            },
            commits);

        var capped = new Dictionary<string, string?>(GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"))
        {
            ["GitHub:MaxPagesPerFetch"] = "2",
        };
        await using var appA = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: capped);

        var firstPass = await GitHubSyncHarness.SyncAsync(appA);
        Assert.False(firstPass.Success, FailureMessage(firstPass));
        Assert.StartsWith("pull_requests:", firstPass.Error, StringComparison.Ordinal);
        Assert.Contains("pull_request_commits", firstPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "pull_requests"));
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "commits"));
        Assert.Equal(string.Empty, await CursorAsync(appA, "pull_requests"));

        var repeatedPass = await GitHubSyncHarness.SyncAsync(appA);
        Assert.False(repeatedPass.Success, FailureMessage(repeatedPass));
        Assert.StartsWith("pull_requests:", repeatedPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "pull_requests"));
        Assert.Equal(string.Empty, await CursorAsync(appA, "pull_requests"));

        var raised = new Dictionary<string, string?>(GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"))
        {
            ["GitHub:MaxPagesPerFetch"] = "3",
        };
        await using var appB = await TracebackApp.RestartAgainstSameDatabaseAsync(
            postgres.Container,
            appA.DatabaseName,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: raised);

        var recovered = AssertSynced(await GitHubSyncHarness.SyncAsync(appB));
        Assert.Equal(1, await GitHubSyncHarness.CountRowsAsync(appB, "pull_requests"));
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(appB, "commits"));
        Assert.NotEqual(string.Empty, await CursorAsync(appB, "pull_requests"));
        Assert.True(recovered.TotalObservationsApplied > 0);
    }

    [Fact]
    public async Task Per_run_artifact_cap_is_atomic_and_repeats_until_raised()
    {
        var world = GitHubSyncHarness.NewWorld();
        var startedAt = TestTimes.Old;
        world.AddRun(
            new FakeRun
            {
                Id = 9101,
                HeadSha = "nested-artifact-run".PadRight(40, 'b'),
                CreatedAt = startedAt,
                UpdatedAt = startedAt,
                RunStartedAt = startedAt,
            },
            Enumerable.Range(1, 5)
                .Select(i => new FakeArtifact { Id = 91010 + i, Name = $"drop-{i}" })
                .ToList());

        var capped = new Dictionary<string, string?>(GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"))
        {
            ["GitHub:MaxPagesPerFetch"] = "2",
        };
        await using var appA = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: capped);

        var firstPass = await GitHubSyncHarness.SyncAsync(appA);
        Assert.False(firstPass.Success, FailureMessage(firstPass));
        Assert.StartsWith("workflow_runs:", firstPass.Error, StringComparison.Ordinal);
        Assert.Contains("workflow_run_artifacts", firstPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "workflow_runs"));
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "build_artifacts"));
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "workflow_run_artifacts"));
        Assert.Equal(string.Empty, await CursorAsync(appA, "workflow_runs"));

        var repeatedPass = await GitHubSyncHarness.SyncAsync(appA);
        Assert.False(repeatedPass.Success, FailureMessage(repeatedPass));
        Assert.StartsWith("workflow_runs:", repeatedPass.Error, StringComparison.Ordinal);
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(appA, "workflow_runs"));
        Assert.Equal(string.Empty, await CursorAsync(appA, "workflow_runs"));

        var raised = new Dictionary<string, string?>(GitHubSyncHarness.DefaultSettings(pageSize: $"{PageSize}"))
        {
            ["GitHub:MaxPagesPerFetch"] = "3",
        };
        await using var appB = await TracebackApp.RestartAgainstSameDatabaseAsync(
            postgres.Container,
            appA.DatabaseName,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: raised);

        var recovered = AssertSynced(await GitHubSyncHarness.SyncAsync(appB));
        Assert.Equal(1, await GitHubSyncHarness.CountRowsAsync(appB, "workflow_runs"));
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(appB, "build_artifacts"));
        Assert.Equal(5, await GitHubSyncHarness.CountRowsAsync(appB, "workflow_run_artifacts"));
        Assert.NotEqual(string.Empty, await CursorAsync(appB, "workflow_runs"));
        Assert.True(recovered.TotalObservationsApplied > 0);
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
