using System.Text.Json;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// The deterministic read APIs over synchronized GitHub data: pull request
/// context, commit delivery context, and the repository change timeline. These
/// run the whole path - fake GitHub responses, connector, normalized events,
/// ingestion, PostgreSQL, HTTP query - and assert that every reported
/// relationship carries the observation that established it.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubReadApiTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset T0 = TestTimes.Old;

    private const string ShaA = "aaaa000000000000000000000000000000000001";
    private const string ShaB = "bbbb000000000000000000000000000000000002";
    private const string ShaC = "cccc000000000000000000000000000000000003";
    private const string ShaD = "dddd000000000000000000000000000000000004";

    private const long BuildRunId = 1001;
    private const long BackportRunId = 1002;

    [Fact]
    public async Task Pull_request_context_returns_commits_runs_artifacts_and_their_evidence()
    {
        await using var app = await StartAsync(World());
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var body = await app.Client.GetJsonAsync("/api/repositories/acme/player-manager/pull-requests/7");

        Assert.Equal("acme/player-manager", body.GetProperty("repositoryKey").GetString());
        var pr = body.GetProperty("pullRequest");
        Assert.Equal(7, pr.GetProperty("number").GetInt32());
        Assert.Equal("Add rate limiter", pr.GetProperty("title").GetString());
        Assert.Equal("open", pr.GetProperty("state").GetString());
        Api.AssertHasSources(pr, "pull request");

        // Commits in author order, each carrying the observation that put it in the PR.
        var commits = body.GetProperty("commits").EnumerateArray().ToList();
        Assert.Equal([ShaA, ShaB, ShaC], commits.Select(c => c.GetProperty("commit").GetProperty("sha").GetString()));
        foreach (var commit in commits)
        {
            Api.AssertHasSources(commit.GetProperty("commit"), "commit");
            var evidence = commit.GetProperty("establishedBy");
            Assert.Equal("github", evidence.GetProperty("provider").GetString());
            Assert.Equal("pull_request", evidence.GetProperty("entityType").GetString());
            Assert.Equal("acme/player-manager#7", evidence.GetProperty("externalKey").GetString());
            Assert.True(evidence.GetProperty("observationSequence").GetInt64() > 0);
        }

        // Only the head commit was built; the run carries its artifact.
        var runsPerCommit = commits.ToDictionary(
            c => c.GetProperty("commit").GetProperty("sha").GetString()!,
            c => c.GetProperty("workflowRuns").EnumerateArray().ToList());
        Assert.Empty(runsPerCommit[ShaA]);
        var headRun = Assert.Single(runsPerCommit[ShaC]);
        Assert.Equal("success", headRun.GetProperty("workflowRun").GetProperty("conclusion").GetString());
        var artifact = Assert.Single(headRun.GetProperty("artifacts").EnumerateArray().ToList());
        Assert.Equal("drop", artifact.GetProperty("name").GetString());
        // GitHub states no image digest for Actions artifacts, so none is invented.
        Assert.True(artifact.GetProperty("digest").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Pull_request_context_is_scoped_to_the_requested_repository()
    {
        var other = new FakeGitHubRepository { Owner = "other-org", Name = "other-repo" };
        other.AddPullRequest(
            new FakePullRequest
            {
                Number = 7,
                Title = "Unrelated work in another repository",
                CreatedAt = T0,
                UpdatedAt = T0,
                HeadSha = "eeee000000000000000000000000000000000005",
            },
            [new FakeCommit { Sha = "eeee000000000000000000000000000000000005", AuthorDate = T0, CommitterDate = T0 }]);

        await using var app = await StartAsync(World(), other);
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));
        AssertSynced(await GitHubSyncHarness.SyncAsync(app, "other-org/other-repo"));

        var mine = await app.Client.GetJsonAsync("/api/repositories/acme/player-manager/pull-requests/7");
        var theirs = await app.Client.GetJsonAsync("/api/repositories/other-org/other-repo/pull-requests/7");

        Assert.Equal("Add rate limiter", mine.GetProperty("pullRequest").GetProperty("title").GetString());
        Assert.Equal("Unrelated work in another repository", theirs.GetProperty("pullRequest").GetProperty("title").GetString());
        Assert.Equal(3, mine.GetProperty("commits").GetArrayLength());
        Assert.Equal(1, theirs.GetProperty("commits").GetArrayLength());

        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            await app.Client.GetStatusAsync("/api/repositories/acme/player-manager/pull-requests/4242"));
    }

    [Fact]
    public async Task Commit_delivery_context_reports_every_pull_request_github_says_contains_it()
    {
        await using var app = await StartAsync(World());
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var body = await app.Client.GetJsonAsync($"/api/repositories/acme/player-manager/commits/{ShaB}/delivery-context");

        Assert.Equal(ShaB, body.GetProperty("sha").GetString());

        // The cherry-picked commit belongs to both pull requests; that is
        // provider evidence from two separate PR commit listings, not a guess.
        var links = body.GetProperty("pullRequests").EnumerateArray().ToList();
        Assert.Equal([7, 8], links.Select(l => l.GetProperty("pullRequest").GetProperty("number").GetInt32()));
        Assert.Equal(
            ["acme/player-manager#7", "acme/player-manager#8"],
            links.Select(l => l.GetProperty("establishedBy").GetProperty("externalKey").GetString()));

        // The backport run executed against this SHA and failed.
        var run = Assert.Single(body.GetProperty("workflowRuns").EnumerateArray().ToList());
        Assert.Equal("completed", run.GetProperty("workflowRun").GetProperty("status").GetString());
        Assert.Equal("failure", run.GetProperty("workflowRun").GetProperty("conclusion").GetString());
        Assert.Empty(run.GetProperty("artifacts").EnumerateArray());

        Assert.Equal(System.Net.HttpStatusCode.NotFound, await app.Client.GetStatusAsync(
            "/api/repositories/acme/player-manager/commits/ffff000000000000000000000000000000000009/delivery-context"));
    }

    [Fact]
    public async Task Repository_changes_pages_through_the_window_without_repeating_or_dropping_entries()
    {
        await using var app = await StartAsync(World());
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var window = $"from={Uri.EscapeDataString(T0.AddDays(-2).ToString("O"))}&to={Uri.EscapeDataString(T0.AddDays(2).ToString("O"))}";

        var all = await app.Client.GetJsonAsync($"/api/repositories/acme/player-manager/changes?{window}&limit=50");
        var expected = Keys(all).ToList();

        // 2 pull requests + 4 commits + 2 workflow runs, newest first.
        Assert.Equal(8, expected.Count);
        Assert.Equal(expected.Count, expected.Distinct().Count());
        Assert.True(all.GetProperty("nextCursor").ValueKind == JsonValueKind.Null);

        var paged = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var suffix = cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}";
            var body = await app.Client.GetJsonAsync($"/api/repositories/acme/player-manager/changes?{window}&limit=3{suffix}");
            paged.AddRange(Keys(body));
            cursor = body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : body.GetProperty("nextCursor").GetString();
            if (cursor is null)
                break;
        }

        Assert.Null(cursor);
        // Paging must reproduce the unpaged order exactly: no repeats, no gaps.
        Assert.Equal(expected, paged);
    }

    [Fact]
    public async Task Repository_listing_and_unknown_repositories_behave_predictably()
    {
        await using var app = await StartAsync(World());
        AssertSynced(await GitHubSyncHarness.SyncAsync(app));

        var repositories = await app.Client.GetJsonAsync("/api/repositories");
        var repo = Assert.Single(repositories.EnumerateArray().ToList());
        Assert.Equal("acme/player-manager", repo.GetProperty("key").GetString());
        Assert.Equal("private", repo.GetProperty("visibility").GetString());

        Assert.Equal(System.Net.HttpStatusCode.NotFound,
            await app.Client.GetStatusAsync("/api/repositories/acme/nope/changes"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            await app.Client.GetStatusAsync("/api/repositories/acme/player-manager/commits/not-hex/delivery-context"));
    }

    private static IEnumerable<string> Keys(JsonElement body) =>
        body.GetProperty("entries").EnumerateArray()
            .Select(e => $"{e.GetProperty("kind").GetString()}:{e.GetProperty("entityId").GetString()}");

    /// <summary>
    /// One pull request with three commits, plus a backport PR that shares the
    /// middle commit (cherry-pick), a successful build of the head commit and a
    /// failed build of the shared commit.
    /// </summary>
    private static FakeGitHubRepository World()
    {
        var world = GitHubSyncHarness.NewWorld();

        var a = Commit(ShaA, "first step", T0.AddHours(-6));
        var b = Commit(ShaB, "the fix", T0.AddHours(-5));
        var c = Commit(ShaC, "tidy up", T0.AddHours(-4));
        var d = Commit(ShaD, "backport wrapper", T0.AddHours(-3));

        world.AddPullRequest(
            new FakePullRequest
            {
                Number = 7,
                Title = "Add rate limiter",
                CreatedAt = T0.AddHours(-7),
                UpdatedAt = T0,
                HeadSha = ShaC,
                HeadRef = "feature/rate-limiter",
            },
            [a, b, c]);

        // The backport shares commit B; adding it directly keeps the default
        // branch listing free of a duplicate entry for that SHA.
        world.PullRequests.Add(new FakePullRequest
        {
            Number = 8,
            Title = "Backport rate limiter",
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-1),
            HeadSha = ShaD,
            HeadRef = "backport/rate-limiter",
            BaseRef = "release/1.x",
        });
        world.PullRequestCommits[8] = [b, d];
        world.Commits.Add(d);

        world.AddRun(
            new FakeRun
            {
                Id = BuildRunId,
                Name = "build",
                HeadSha = ShaC,
                Status = "completed",
                Conclusion = "success",
                CreatedAt = T0.AddHours(-4),
                RunStartedAt = T0.AddHours(-4),
                UpdatedAt = T0.AddHours(-4).AddMinutes(6),
            },
            [new FakeArtifact { Id = 5001, Name = "drop" }]);

        world.AddRun(new FakeRun
        {
            Id = BackportRunId,
            Name = "build",
            HeadSha = ShaB,
            HeadBranch = "release/1.x",
            Status = "completed",
            Conclusion = "failure",
            CreatedAt = T0.AddHours(-2),
            RunStartedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2).AddMinutes(4),
        });

        return world;
    }

    private static FakeCommit Commit(string sha, string message, DateTimeOffset at) =>
        new() { Sha = sha, Message = message, AuthorDate = at, CommitterDate = at };

    private Task<TracebackApp> StartAsync(params FakeGitHubRepository[] worlds) =>
        TracebackApp.StartAsync(
            postgres.Container,
            seedFixturesOnStartup: false,
            configureServices: GitHubSyncHarness.WireFakeTransport(worlds),
            settings: GitHubSyncHarness.DefaultSettings());

    private static RepositorySyncResult AssertSynced(RepositorySyncResult result)
    {
        Assert.True(result.Success,
            $"sync of '{result.RepositoryKey}' failed: {result.Error} [" +
            string.Join("; ", result.Resources.Select(r => $"{r.ResourceType}: {r.Error ?? "ok"}")) + "]");
        return result;
    }
}
