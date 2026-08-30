using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Partial failure during synchronization: a failing resource stream stops the
/// run without advancing its checkpoint or running later streams, earlier
/// streams stay durably applied with their errors recorded per-stream, a
/// single transient failure is retried transparently within one pass, and the
/// next healthy pass resumes from the unchanged cursor so the failure window
/// skips nothing - including data that arrived while the stream was down.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubPartialFailureTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset T0 = TestTimes.Old;

    /// <summary>
    /// Commits and a run, no pull requests, so the '/commits' route fragment
    /// can only match the standalone commits listing.
    /// </summary>
    private static FakeGitHubRepository NewCommitsWorld() => NewWorldWithCommits(("seed000000000000000000000000000000000000a1", T0.AddHours(-2)), ("seed000000000000000000000000000000000000a2", T0.AddHours(-1)));

    private static FakeGitHubRepository NewWorldWithCommits(params (string Sha, DateTimeOffset CommittedAt)[] commits)
    {
        var world = GitHubSyncHarness.NewWorld();
        foreach (var (sha, committedAt) in commits)
            world.Commits.Add(new FakeCommit { Sha = sha, AuthorDate = committedAt, CommitterDate = committedAt });
        return world;
    }

    [Fact]
    public async Task Failing_stream_stops_the_run_and_preserves_earlier_streams()
    {
        var world = NewCommitsWorld();
        var run = new FakeRun
        {
            Id = 900,
            HeadSha = "run00000000000000000000000000000000000009",
            Status = "completed",
            Conclusion = "success",
            CreatedAt = T0,
            UpdatedAt = T0,
            RunStartedAt = T0,
        };
        world.AddRun(run);

        var (app, handler) = await StartWithAsync(postgres.Container, world);
        await using var _ = app;
        handler.FailRouteContaining = "/commits";
        handler.FailRouteStatus = 500;

        var failed = await GitHubSyncHarness.SyncAsync(app);

        // The run fails at the commits stream and reports it.
        Assert.False(failed.Success);
        Assert.StartsWith("commits:", failed.Error, StringComparison.Ordinal);
        Assert.Equal(["repository", "pull_requests", "commits"], failed.Resources.Select(r => r.ResourceType).ToArray());
        Assert.Contains("500", Outcome(failed, "commits").Error, StringComparison.Ordinal);

        // Earlier streams are durable; later streams never ran.
        Assert.Equal(1, await CountAsync(app, "source_repositories"));
        Assert.Equal(0, await CountAsync(app, "commits"));
        Assert.Equal(0, await CountAsync(app, "workflow_runs"));

        // The failing stream kept its cursor and recorded its error; the
        // healthy streams did not.
        Assert.Equal(string.Empty, await RawCursorAsync(app, "commits"));
        Assert.True(await ColumnIsNotNullAsync(app, "commits", "last_error"));
        Assert.False(await ColumnIsNotNullAsync(app, "pull_requests", "last_error"));

        // Transient handling exhausted: initial attempt plus MaxRetries=3.
        Assert.Equal(4, handler.RequestLog.Count(p => p.Contains("/commits?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Recovery_pass_resumes_from_the_unchanged_cursor_and_catches_up()
    {
        var world = NewCommitsWorld();
        var (app, handler) = await StartWithAsync(postgres.Container, world);
        await using var _ = app;

        handler.FailRouteContaining = "/commits";
        Assert.False((await GitHubSyncHarness.SyncAsync(app)).Success);

        // While the commits stream was down, new history landed upstream.
        const string gapSha = "gapsha00000000000000000000000000000000aa";
        world.Commits.Add(new FakeCommit
        {
            Sha = gapSha,
            AuthorDate = T0.AddMinutes(30),
            CommitterDate = T0.AddMinutes(30),
        });

        handler.FailRouteContaining = null;
        var recovered = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        // The resume re-walked from the old watermark: nothing skipped.
        Assert.True(recovered.TotalObservationsApplied >= 1);
        foreach (var sha in world.Commits.Select(c => c.Sha))
            Assert.Equal(1, await CountShaAsync(app, sha));
        Assert.Equal(T0.AddMinutes(30), await CursorAsync(app, "commits", "since"));
    }

    [Fact]
    public async Task Single_transient_failure_is_retried_within_one_pass()
    {
        var world = NewCommitsWorld();
        var (app, handler) = await StartWithAsync(postgres.Container, world);
        await using var _ = app;

        // One 500 queued: it hits the first request (the repository fetch),
        // which the client retries transparently.
        handler.ScriptFailure(500);

        var result = AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        Assert.Equal(1, await CountAsync(app, "source_repositories"));
        Assert.Equal(2, handler.RequestLog.Count(p => p.EndsWith("/repos/acme/player-manager", StringComparison.Ordinal)));
        Assert.True(result.TotalObservationsApplied > 0);
    }

    private static ResourceSyncOutcome Outcome(RepositorySyncResult result, string resourceType) =>
        result.Resources.Single(r => r.ResourceType == resourceType);

    [Fact]
    public async Task Ingest_failure_writes_nothing_and_leaves_the_checkpoint_where_it_was()
    {
        // A provider value that cannot be stored (commits.sha is varchar(64))
        // makes ingestion fail after the connector has already produced events
        // for the whole stream - the case where a rolled-back batch is still
        // sitting in the change tracker when the failure is recorded.
        var world = NewWorldWithCommits(("good0000000000000000000000000000000000a1", T0.AddHours(-2)));
        var unstorable = new FakeCommit
        {
            Sha = new string('f', 200),
            AuthorDate = T0.AddHours(-1),
            CommitterDate = T0.AddHours(-1),
        };
        world.Commits.Add(unstorable);

        var (app, _) = await StartWithAsync(postgres.Container, world);
        await using var _disposable = app;

        var failed = await GitHubSyncHarness.SyncAsync(app);

        Assert.False(failed.Success);
        Assert.StartsWith("commits:", failed.Error, StringComparison.Ordinal);

        // The whole batch rolled back: neither the valid commit nor its
        // observation survived, and no half-written rows leaked out.
        Assert.Equal(0, await GitHubSyncHarness.CountRowsAsync(app, "commits"));
        var observations = await GitHubSyncHarness.QueryAsync(app,
            "SELECT count(*) FROM observations WHERE entity_type_name = 'commit'");
        Assert.Equal("0", Assert.Single(observations));

        // The checkpoint records the failure and does not advance.
        var cursor = await GitHubSyncHarness.QueryAsync(app,
            "SELECT coalesce(cursor, '') FROM sync_states WHERE integration_id = $1 AND resource_type = 'commits'",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}");
        Assert.Equal("", Assert.Single(cursor));
        var error = await GitHubSyncHarness.QueryAsync(app,
            "SELECT coalesce(last_error, '') FROM sync_states WHERE integration_id = $1 AND resource_type = 'commits'",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}");
        Assert.NotEqual("", Assert.Single(error));

        // Once the provider stops sending the unstorable value, the next pass
        // imports the window it skipped.
        world.Commits.Remove(unstorable);
        var recovered = await GitHubSyncHarness.SyncAsync(app);
        Assert.True(recovered.Success, recovered.Error);
        Assert.Equal(1, await GitHubSyncHarness.CountRowsAsync(app, "commits"));
    }

    private static async Task<(TracebackApp App, FakeGitHubApiHandler Handler)> StartWithAsync(
        PostgreSqlContainer container, FakeGitHubRepository world)
    {
        var handler = new FakeGitHubApiHandler { Repository = world };
        var app = await TracebackApp.StartAsync(
            container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(handler),
            settings: GitHubSyncHarness.DefaultSettings());
        return (app, handler);
    }

    private static async Task<int> CountAsync(TracebackApp app, string table)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, $"SELECT count(*) FROM {table}");
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountShaAsync(TracebackApp app, string sha)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app, "SELECT count(*) FROM commits WHERE sha = $1", sha);
        return int.Parse(Assert.Single(rows), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> RawCursorAsync(TracebackApp app, string resourceType)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT cursor FROM sync_states WHERE integration_id = $1 AND resource_type = $2",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", resourceType);
        return Assert.Single(rows);
    }

    private static async Task<bool> ColumnIsNotNullAsync(TracebackApp app, string resourceType, string column)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            $"SELECT {column} IS NOT NULL FROM sync_states WHERE integration_id = $1 AND resource_type = $2",
            $"github/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", resourceType);
        return Assert.Single(rows) == bool.TrueString;
    }

    private static async Task<System.DateTimeOffset?> CursorAsync(TracebackApp app, string resourceType, string property)
    {
        var raw = await RawCursorAsync(app, resourceType);
        if (raw.Length == 0)
            return null;
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty(property).GetDateTimeOffset();
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
