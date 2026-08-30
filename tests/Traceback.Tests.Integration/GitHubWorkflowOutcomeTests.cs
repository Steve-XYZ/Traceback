using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Workflow run outcomes are stored as GitHub states them. Traceback does not
/// collapse "cancelled", "timed_out" or "skipped" into failure, and each run is
/// attached to the commit named by its head SHA rather than to whatever commit
/// happens to be newest.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubWorkflowOutcomeTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset T0 = TestTimes.Old;

    private static readonly (long RunId, string Sha, string Status, string? Conclusion)[] Outcomes =
    [
        (2001, "1111000000000000000000000000000000000001", "completed", "success"),
        (2002, "2222000000000000000000000000000000000002", "completed", "failure"),
        (2003, "3333000000000000000000000000000000000003", "completed", "cancelled"),
        (2004, "4444000000000000000000000000000000000004", "completed", "timed_out"),
        (2005, "5555000000000000000000000000000000000005", "in_progress", null),
    ];

    [Fact]
    public async Task Each_outcome_is_stored_verbatim_against_the_commit_its_head_sha_names()
    {
        var world = GitHubSyncHarness.NewWorld();
        foreach (var (runId, sha, status, conclusion) in Outcomes)
        {
            world.Commits.Add(new FakeCommit { Sha = sha, Message = $"commit for {runId}", AuthorDate = T0.AddHours(-1), CommitterDate = T0.AddHours(-1) });
            world.AddRun(new FakeRun
            {
                Id = runId,
                HeadSha = sha,
                Status = status,
                Conclusion = conclusion,
                CreatedAt = T0,
                RunStartedAt = T0,
                UpdatedAt = T0.AddMinutes(5),
            });
        }

        await using var app = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: GitHubSyncHarness.DefaultSettings());

        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        foreach (var (runId, sha, status, conclusion) in Outcomes)
        {
            var row = await RunRowAsync(app, runId);
            Assert.Equal(status, row.Status);
            // "cancelled" and "timed_out" survive as themselves; an unfinished
            // run has no conclusion rather than a guessed one.
            Assert.Equal(conclusion ?? "", row.Conclusion);
            Assert.Equal(sha, row.CommitSha);
        }
    }

    [Fact]
    public async Task Cancelled_run_is_reported_by_the_delivery_context_of_its_commit()
    {
        const string sha = "3333000000000000000000000000000000000003";
        var world = GitHubSyncHarness.NewWorld();
        world.Commits.Add(new FakeCommit { Sha = sha, Message = "cancelled build", AuthorDate = T0.AddHours(-1), CommitterDate = T0.AddHours(-1) });
        world.AddRun(new FakeRun
        {
            Id = 2003,
            HeadSha = sha,
            Status = "completed",
            Conclusion = "cancelled",
            CreatedAt = T0,
            RunStartedAt = T0,
            UpdatedAt = T0.AddMinutes(2),
        });

        await using var app = await TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(world),
            settings: GitHubSyncHarness.DefaultSettings());
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var body = await app.Client.GetJsonAsync(
            $"/api/repositories/{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}/commits/{sha}/delivery-context");

        var run = Assert.Single(body.GetProperty("workflowRuns").EnumerateArray().ToList());
        Assert.Equal("cancelled", run.GetProperty("workflowRun").GetProperty("conclusion").GetString());
        // A cancelled run produced nothing: no artifact is inferred for it.
        Assert.Empty(run.GetProperty("artifacts").EnumerateArray());
        Assert.Empty(body.GetProperty("pullRequests").EnumerateArray());
    }

    private static async Task<(string Status, string Conclusion, string CommitSha)> RunRowAsync(TracebackApp app, long runId)
    {
        var rows = await GitHubSyncHarness.QueryAsync(app,
            "SELECT wr.status || '|' || coalesce(wr.conclusion, '') || '|' || coalesce(c.sha, '') " +
            "FROM workflow_runs wr " +
            "JOIN source_repositories sr ON sr.id = wr.source_repository_id " +
            "LEFT JOIN commits c ON c.id = wr.commit_id " +
            "WHERE sr.key = $1 AND wr.run_id = $2",
            $"{GitHubSyncHarness.Owner}/{GitHubSyncHarness.Name}", runId);
        var parts = Assert.Single(rows).Split('|');
        return (parts[0], parts[1], parts[2]);
    }

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
