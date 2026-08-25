using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Pull request lifecycle across synchronization passes: transitions apply as
/// new observations and update exactly one row per external PR; deliveries
/// whose provider-state timestamp is older than what was last applied cannot
/// revert newer facts; and the incremental walk inspects only the watermark
/// minus overlap window, so updates landing inside that window are captured
/// while long-stale pull requests are left alone.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubPullRequestLifecycleTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset T0 = TestTimes.Old;

    [Fact]
    public async Task Open_pull_request_merging_in_a_later_pass_updates_one_row()
    {
        var world = GitHubSyncHarness.NewWorld();
        var pr = SeedPullRequest(world, number: 7, updatedAt: T0);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal("open", await PrScalarAsync(app, "state", 7));

        pr.State = "closed";
        pr.MergedAt = T0.AddHours(2);
        pr.UpdatedAt = T0.AddHours(2);

        var mergedPass = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // One row per external PR; the transition arrives as a new observation.
        Assert.Equal(1, await CountPrsAsync(app, number: 7));
        Assert.Equal("merged", await PrScalarAsync(app, "state", 7));
        Assert.True(await PrHasValueAsync(app, "merged_at", 7));
        var prOutcome = mergedPass.Resources.Single(r => r.ResourceType == "pull_requests");
        Assert.Equal(1, prOutcome.ObservationsApplied);
    }

    [Fact]
    public async Task Closing_without_merge_reports_closed_and_keeps_closed_at()
    {
        var world = GitHubSyncHarness.NewWorld();
        var pr = SeedPullRequest(world, number: 7, updatedAt: T0);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        pr.State = "closed";
        pr.ClosedAt = T0.AddHours(1);
        pr.UpdatedAt = T0.AddHours(1);

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal("closed", await PrScalarAsync(app, "state", 7));
        Assert.True(await PrHasValueAsync(app, "closed_at", 7));
        Assert.False(await PrHasValueAsync(app, "merged_at", 7));
    }

    [Fact]
    public async Task Stale_delivery_cannot_revert_a_merged_pull_request()
    {
        var world = GitHubSyncHarness.NewWorld();
        var pr = SeedPullRequest(world, number: 7, updatedAt: T0);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);

        // First pass observes the PR already merged (updated_at = merge time).
        pr.State = "closed";
        pr.MergedAt = T0;
        pr.UpdatedAt = T0;
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal("merged", await PrScalarAsync(app, "state", 7));
        var observationsAfterMerge = await GitHubSyncHarness.CountRowsAsync(app, "observations");

        // A stale snapshot (pre-merge updated_at) arrives out of order, e.g.
        // replayed from a cached listing page. Its content differs, so it is
        // recorded as an observation - but it must not revert domain state.
        pr.State = "open";
        pr.MergedAt = null;
        pr.UpdatedAt = T0.AddHours(-1);

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal("merged", await PrScalarAsync(app, "state", 7));
        Assert.True(await PrHasValueAsync(app, "merged_at", 7));
        Assert.Equal(observationsAfterMerge + 1, await GitHubSyncHarness.CountRowsAsync(app, "observations"));
    }

    [Fact]
    public async Task Incremental_walk_inspects_only_back_to_the_overlap_floor()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 1, updatedAt: T0);
        SeedPullRequest(world, number: 2, updatedAt: T0.AddDays(-1));
        // Long-stale PRs sit ten days behind the newest update.
        SeedPullRequest(world, number: 3, updatedAt: T0.AddDays(-10));
        SeedPullRequest(world, number: 4, updatedAt: T0.AddDays(-10));
        SeedPullRequest(world, number: 5, updatedAt: T0.AddDays(-10));

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var incremental = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // The walk stops at the first item older than watermark minus the
        // seven-day overlap. The terminating stale item still counts as
        // inspected, so three of five PRs are examined. Each examined PR
        // delivers two events (its head commit and the PR itself), all
        // absorbed as duplicates.
        var prOutcome = incremental.Resources.Single(r => r.ResourceType == "pull_requests");
        Assert.Equal(3, prOutcome.Inspected);
        Assert.Equal(0, prOutcome.ObservationsApplied);
        Assert.Equal(4, prOutcome.Duplicated);
        Assert.Equal(5, await CountPrsTotalAsync(app));
    }

    [Fact]
    public async Task Update_landing_inside_the_overlap_window_is_captured()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 1, updatedAt: T0);
        SeedPullRequest(world, number: 2, updatedAt: T0.AddDays(-1));
        var stale = SeedPullRequest(world, number: 3, updatedAt: T0.AddDays(-10));
        SeedPullRequest(world, number: 4, updatedAt: T0.AddDays(-10));
        SeedPullRequest(world, number: 5, updatedAt: T0.AddDays(-10));

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // The stale PR moves three days behind the watermark - inside the
        // seven-day overlap window - with a new title and an added commit.
        var pushedSha = "pushed0000000000000000000000000000000000aa";
        stale.Title = "Renamed: feature work";
        stale.UpdatedAt = T0.AddDays(-3);
        world.PullRequestCommits[3].Add(new FakeCommit
        {
            Sha = pushedSha,
            AuthorDate = stale.UpdatedAt,
            CommitterDate = stale.UpdatedAt,
        });
        world.Commits.Add(world.PullRequestCommits[3][^1]);

        var incremental = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // PRs 1-3 are inspected; PR 4 is the stale terminator (counted, not
        // ingested); PR 5 is never reached.
        var prOutcome = incremental.Resources.Single(r => r.ResourceType == "pull_requests");
        Assert.Equal(4, prOutcome.Inspected);

        Assert.Equal("Renamed: feature work", await PrScalarAsync(app, "title", 3));
        Assert.True(await PrContainsCommitAsync(app, 3, pushedSha));
    }

    private static async Task<TracebackApp> StartWithWorldsAsync(
        PostgreSqlContainer container, params FakeGitHubRepository[] worlds) =>
        await TracebackApp.StartAsync(
            container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(worlds),
            settings: GitHubSyncHarness.DefaultSettings());

    private static FakePullRequest SeedPullRequest(FakeGitHubRepository world, int number, DateTimeOffset updatedAt)
    {
        var sha = $"life{number:d4}".PadRight(40, 'a');
        return world.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR #{number}",
                CreatedAt = updatedAt.AddDays(-1),
                UpdatedAt = updatedAt,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = updatedAt.AddDays(-1), CommitterDate = updatedAt.AddDays(-1) }]);
    }

    private static async Task<string> PrScalarAsync(TracebackApp app, string column, int number)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            $"SELECT p.{column} FROM pull_requests p JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "WHERE sr.key = $1 AND p.number = $2",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", number);
        return Assert.Single(rows);
    }

    private static async Task<bool> PrHasValueAsync(TracebackApp app, string column, int number)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            $"SELECT p.{column} IS NOT NULL FROM pull_requests p JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "WHERE sr.key = $1 AND p.number = $2",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", number);
        return Assert.Single(rows) == bool.TrueString;
    }

    private static async Task<int> CountPrsAsync(TracebackApp app, int number)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT count(*) FROM pull_requests p JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "WHERE sr.key = $1 AND p.number = $2",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", number);
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountPrsTotalAsync(TracebackApp app)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, "SELECT count(*) FROM pull_requests");
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> PrContainsCommitAsync(TracebackApp app, int number, string sha)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT count(*) FROM pull_request_commits pc " +
            "JOIN pull_requests p ON p.id = pc.pull_request_id " +
            "JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "JOIN commits c ON c.id = pc.commit_id " +
            "WHERE sr.key = $1 AND p.number = $2 AND c.sha = $3",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", number, sha);
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
