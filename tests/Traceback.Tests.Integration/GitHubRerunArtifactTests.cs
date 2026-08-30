using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Actions reruns and artifacts: every rerun attempt keeps its own historical
/// row (reruns never reduce a run to its latest attempt), artifacts attach to
/// a run only when that attempt is unambiguous, an in-progress run completes in
/// place rather than spawning a second row, and artifacts published after a
/// run are picked up by a later pass.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubRerunArtifactTests(PostgresContainerFixture postgres)
{
    private const long RunId = 900;

    private static readonly DateTimeOffset T0 = TestTimes.Old;

    [Fact]
    public async Task Both_attempts_are_ingested_when_a_rerun_is_seen_on_the_first_pass()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedRunWithAttempts(world,
            attempt1: Attempt(1, conclusion: "success", updatedAt: T0),
            attempt2: Attempt(2, conclusion: "failure", updatedAt: T0.AddHours(1)));
        world.AddRun(runsListingEntry(world), [new FakeArtifact { Id = 500, Name = "drop" }]);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);

        // One historical row per (run id, attempt). The artifact is retained,
        // but its logical run id cannot identify either attempt.
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal(2, await CountRunsAsync(app));
        Assert.Equal([1, 2], await RunAttemptsAsync(app, RunId));
        Assert.Equal("failure", await RunScalarAsync(app, "conclusion", RunId, attempt: 2));
        Assert.Empty(await ArtifactEdgeAttemptsAsync(app, RunId));
        Assert.Equal(1, await CountArtifactsAsync(app));
    }

    [Fact]
    public async Task Rerun_in_a_later_pass_adds_an_attempt_row_without_rewriting_history()
    {
        var world = GitHubSyncHarness.NewWorld();
        var first = Attempt(1, conclusion: "success", updatedAt: T0);
        world.AddRun(first, [new FakeArtifact { Id = 500, Name = "drop" }]);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal([1], await RunAttemptsAsync(app, RunId));

        // The run is retried: attempt 2 fails while attempt 1 stays as it was.
        var retry = Attempt(2, conclusion: "failure", updatedAt: T0.AddHours(1));
        world.AddRunAttempt(first);
        world.AddRunAttempt(retry);
        world.Runs.Clear();
        world.AddRun(retry);

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal(2, await CountRunsAsync(app));
        Assert.Equal("success", await RunScalarAsync(app, "conclusion", RunId, attempt: 1));
        Assert.Equal("failure", await RunScalarAsync(app, "conclusion", RunId, attempt: 2));
        // The first pass had one known attempt, so its edge remains. Once the
        // rerun is visible, the logical artifact response cannot justify a new
        // edge to attempt 2.
        Assert.Equal([1], await ArtifactEdgeAttemptsAsync(app, RunId));
        Assert.Equal(1, await CountArtifactsAsync(app));
    }

    [Fact]
    public async Task In_progress_run_completing_later_updates_its_row_without_duplicating()
    {
        var world = GitHubSyncHarness.NewWorld();
        var running = Attempt(1, conclusion: null, updatedAt: T0);
        running.Status = "in_progress";
        world.AddRun(running);

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // updated_at alone must not project onto completed_at.
        Assert.Equal(1, await CountRunsAsync(app));
        Assert.Equal("in_progress", await RunScalarAsync(app, "status", RunId, attempt: 1));
        Assert.False(await RunHasValueAsync(app, "completed_at", RunId, attempt: 1));

        running.Status = "completed";
        running.Conclusion = "success";
        running.UpdatedAt = T0.AddMinutes(30);

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal(1, await CountRunsAsync(app));
        Assert.Equal("completed", await RunScalarAsync(app, "status", RunId, attempt: 1));
        Assert.True(await RunHasValueAsync(app, "completed_at", RunId, attempt: 1));
    }

    [Fact]
    public async Task Artifact_published_after_the_run_appears_on_a_later_pass()
    {
        var world = GitHubSyncHarness.NewWorld();
        world.AddRun(Attempt(1, conclusion: "success", updatedAt: T0));

        await using var app = await StartWithWorldsAsync(postgres.Container, world);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        Assert.Equal(0, await CountArtifactsAsync(app));

        world.Artifacts[RunId] = [new FakeArtifact { Id = 600, Name = "late-drop" }];

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal(1, await CountArtifactsAsync(app));
        Assert.Equal([1], await ArtifactEdgeAttemptsAsync(app, RunId));
    }

    private static FakeRun Attempt(int number, string? conclusion, DateTimeOffset updatedAt) => new()
    {
        Id = RunId,
        RunAttempt = number,
        HeadSha = $"sha{number:d4}".PadRight(40, 'a'),
        Status = conclusion is null ? "in_progress" : "completed",
        Conclusion = conclusion,
        CreatedAt = T0,
        UpdatedAt = updatedAt,
        RunStartedAt = T0,
    };

    /// <summary>The runs listing exposes exactly one entry per run id: the latest attempt.</summary>
    private static FakeRun runsListingEntry(FakeGitHubRepository world) =>
        world.RunAttempts.Values.SelectMany(list => list).OrderByDescending(r => r.RunAttempt).First();

    private static void SeedRunWithAttempts(FakeGitHubRepository world, FakeRun attempt1, FakeRun attempt2)
    {
        world.AddRunAttempt(attempt1);
        world.AddRunAttempt(attempt2);
    }

    private static async Task<TracebackApp> StartWithWorldsAsync(
        PostgreSqlContainer container, params FakeGitHubRepository[] worlds) =>
        await TracebackApp.StartAsync(
            container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(worlds),
            settings: GitHubSyncHarness.DefaultSettings());

    private static async Task<int> CountRunsAsync(TracebackApp app)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, "SELECT count(*) FROM workflow_runs");
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<int>> RunAttemptsAsync(TracebackApp app, long runId)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT wr.run_attempt FROM workflow_runs wr JOIN source_repositories sr ON sr.id = wr.source_repository_id " +
            "WHERE sr.key = $1 AND wr.run_id = $2 ORDER BY wr.run_attempt",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", runId);
        return rows.Select(int.Parse).ToList();
    }

    /// <summary>Attempts of the run that carry at least one artifact edge.</summary>
    private static async Task<List<int>> ArtifactEdgeAttemptsAsync(TracebackApp app, long runId)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT DISTINCT wr.run_attempt FROM workflow_run_artifacts wra " +
            "JOIN workflow_runs wr ON wr.id = wra.workflow_run_id " +
            "JOIN source_repositories sr ON sr.id = wr.source_repository_id " +
            "WHERE sr.key = $1 AND wr.run_id = $2 ORDER BY wr.run_attempt",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", runId);
        return rows.Select(int.Parse).ToList();
    }

    private static async Task<int> CountArtifactsAsync(TracebackApp app)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, "SELECT count(*) FROM build_artifacts");
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> RunScalarAsync(TracebackApp app, string column, long runId, int attempt)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            $"SELECT wr.{column} FROM workflow_runs wr JOIN source_repositories sr ON sr.id = wr.source_repository_id " +
            "WHERE sr.key = $1 AND wr.run_id = $2 AND wr.run_attempt = $3",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", runId, attempt);
        return Assert.Single(rows);
    }

    private static async Task<bool> RunHasValueAsync(TracebackApp app, string column, long runId, int attempt)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            $"SELECT wr.{column} IS NOT NULL FROM workflow_runs wr JOIN source_repositories sr ON sr.id = wr.source_repository_id " +
            "WHERE sr.key = $1 AND wr.run_id = $2 AND wr.run_attempt = $3",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", runId, attempt);
        return Assert.Single(rows) == bool.TrueString;
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
