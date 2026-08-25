using System.Text.Json;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Incremental checkpointing across three synchronization passes: the first
/// pass ingests lookback history and records per-stream watermarks, an
/// unchanged second pass absorbs the entire overlap window as duplicates and
/// leaves every cursor untouched, and a third pass applies only genuinely new
/// provider facts while advancing watermarks to the freshest provider
/// timestamps - never to wall-clock time.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubIncrementalCheckpointTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset T0 = TestTimes.Old;

    [Fact]
    public async Task Three_passes_record_watermarks_absorb_idle_windows_and_advance_on_activity()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 1, updatedAt: T0, committedAt: T0.AddHours(-1));
        SeedPullRequest(world, number: 2, updatedAt: T0.AddDays(-1), committedAt: T0.AddDays(-1).AddHours(-1));
        world.AddRun(new FakeRun
        {
            Id = 900,
            HeadSha = "run00000000000000000000000000000000000009",
            Status = "completed",
            Conclusion = "success",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            RunStartedAt = T0.AddHours(-2),
        });

        await using var app = await StartWithWorldsAsync(postgres.Container, world);

        // Pass 1 - initial history: watermarks land on provider timestamps.
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal(T0, await CursorAsync(app, "pull_requests", "notBefore"));
        // The newest commit is PR 1's head commit (T0 minus one hour).
        Assert.Equal(T0.AddHours(-1), await CursorAsync(app, "commits", "since"));
        Assert.Equal(T0.AddHours(-2), await CursorAsync(app, "workflow_runs", "createdFrom"));

        // Pass 2 - nothing changed upstream: the overlap window is redelivered
        // in full and absorbed; no cursor moves.
        var idlePass = AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal(0, idlePass.TotalObservationsApplied);
        Assert.Equal(8, idlePass.TotalDuplicates);
        Assert.Equal(T0, await CursorAsync(app, "pull_requests", "notBefore"));
        Assert.Equal(T0.AddHours(-1), await CursorAsync(app, "commits", "since"));
        Assert.Equal(T0.AddHours(-2), await CursorAsync(app, "workflow_runs", "createdFrom"));

        // Pass 3 - upstream activity: a renamed PR, a fresh commit, a new run.
        world.PullRequests[0].Title = "PR #1 v2";
        world.PullRequests[0].UpdatedAt = T0.AddHours(1);
        world.Commits.Add(new FakeCommit
        {
            Sha = "newcommit00000000000000000000000000000000aa",
            AuthorDate = T0.AddHours(2),
            CommitterDate = T0.AddHours(2),
        });
        world.AddRun(new FakeRun
        {
            Id = 901,
            HeadSha = "newcommit00000000000000000000000000000000aa",
            Status = "completed",
            Conclusion = "success",
            CreatedAt = T0.AddHours(2),
            UpdatedAt = T0.AddHours(2),
            RunStartedAt = T0.AddHours(2),
        });

        var activePass = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // Exactly one new fact per touched stream; unchanged neighbors inside
        // the window stay duplicates.
        Assert.Equal(1, Outcome(activePass, "pull_requests").ObservationsApplied);
        Assert.Equal(1, Outcome(activePass, "commits").ObservationsApplied);
        Assert.Equal(1, Outcome(activePass, "workflow_runs").ObservationsApplied);
        Assert.Equal("PR #1 v2", await ScalarAsync(app,
            "SELECT p.title FROM pull_requests p JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "WHERE sr.key = $1 AND p.number = $2",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", 1));

        // Watermarks advanced to the freshest provider facts, not to now.
        Assert.Equal(T0.AddHours(1), await CursorAsync(app, "pull_requests", "notBefore"));
        Assert.Equal(T0.AddHours(2), await CursorAsync(app, "commits", "since"));
        Assert.Equal(T0.AddHours(2), await CursorAsync(app, "workflow_runs", "createdFrom"));
    }

    [Fact]
    public async Task Late_arriving_commit_inside_the_overlap_window_is_captured_without_moving_the_watermark()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 1, updatedAt: T0, committedAt: T0.AddHours(-1));

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        var watermarkAfterFirst = await CursorAsync(app, "commits", "since");
        Assert.Equal(T0.AddHours(-1), watermarkAfterFirst);
        Assert.NotNull(watermarkAfterFirst);

        // A commit authored six hours before the watermark appears late (e.g.
        // force-push archaeology). It sits inside the seven-day overlap.
        var lateSha = "latecommit0000000000000000000000000000000a";
        world.Commits.Add(new FakeCommit
        {
            Sha = lateSha,
            AuthorDate = watermarkAfterFirst.Value.AddHours(-6),
            CommitterDate = watermarkAfterFirst.Value.AddHours(-6),
        });

        var secondPass = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var commitsOutcome = Outcome(secondPass, "commits");
        Assert.Equal(1, commitsOutcome.ObservationsApplied);
        Assert.Equal(watermarkAfterFirst, await CursorAsync(app, "commits", "since"));
    }

    private static ResourceSyncOutcome Outcome(RepositorySyncResult result, string resourceType) =>
        result.Resources.Single(r => r.ResourceType == resourceType);

    private static async Task<TracebackApp> StartWithWorldsAsync(
        PostgreSqlContainer container, params FakeGitHubRepository[] worlds) =>
        await TracebackApp.StartAsync(
            container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(worlds),
            settings: GitHubSyncHarness.DefaultSettings());

    private static FakeGitHubRepository SeedPullRequest(
        FakeGitHubRepository world, int number, DateTimeOffset updatedAt, DateTimeOffset committedAt)
    {
        var sha = $"chk{number:d4}".PadRight(40, 'a');
        world.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR #{number}",
                CreatedAt = updatedAt.AddDays(-1),
                UpdatedAt = updatedAt,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = committedAt, CommitterDate = committedAt }]);
        return world;
    }

    /// <summary>Reads a typed property out of the stored checkpoint JSON.</summary>
    private static async Task<DateTimeOffset?> CursorAsync(TracebackApp app, string resourceType, string property)
    {
        var raw = await RawCursorAsync(app, resourceType);
        if (raw.Length == 0)
            return null;
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty(property).GetDateTimeOffset();
    }

    private static async Task<string> RawCursorAsync(TracebackApp app, string resourceType)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT cursor FROM sync_states WHERE integration_id = $1 AND resource_type = $2",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", resourceType);
        return Assert.Single(rows);
    }

    private static async Task<string> ScalarAsync(TracebackApp app, string sql, params object[] parameters)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, sql, parameters);
        return Assert.Single(rows);
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
