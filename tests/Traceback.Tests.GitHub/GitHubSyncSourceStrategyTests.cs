using Microsoft.Extensions.Options;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.GitHub;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.GitHub;

/// <summary>
/// Stream-level synchronization strategy: pagination without truncation,
/// initial-lookback windows, incremental watermarks, rerun attempt
/// enumeration, and no-advance-on-truncation.
/// </summary>
public sealed class GitHubSyncSourceStrategyTests : IDisposable
{
    private readonly FakeGitHubApiHandler _handler;
    private readonly GitHubRepositorySyncSource _source;
    private readonly GitHubConnectorOptions _options = new()
    {
        PageSize = 2,
        MaxPagesPerFetch = 200,
        InitialLookbackDays = 30,
        IncrementalOverlapDays = 7,
    };

    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    public GitHubSyncSourceStrategyTests()
    {
        var world = new FakeGitHubRepository { Owner = "acme", Name = "player-manager" };
        _handler = new FakeGitHubApiHandler { Repository = world };
        World = world;
        _source = new GitHubRepositorySyncSource(
            CreateClient(),
            new TestHolder(_options));
    }

    public void Dispose() => _handler.Dispose();

    private FakeGitHubRepository World { get; }

    private IGitHubApiClient CreateClient()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.test/") };
        return new GitHubRestClient(httpClient, new StaticTokenProvider("token"), new TestOptionsMonitor<GitHubConnectorOptions>(_options));
    }

    [Fact]
    public async Task All_pages_are_walked_without_silent_truncation()
    {
        for (var i = 1; i <= 5; i++)
            AddPullRequest(i, daysAgo: i);

        var result = await _source.FetchAsync(Fetch("pull_requests", cursor: null));

        Assert.Equal(5, result.InspectedCount);
        var prEvents = result.Events.OfType<PullRequestObserved>().ToList();
        Assert.Equal(5, prEvents.Count);
        // Every PR also carries its commit membership evidence.
        Assert.All(prEvents, e => Assert.Single(e.CommitShas));
    }

    [Fact]
    public async Task Initial_pass_respects_the_lookback_window()
    {
        AddPullRequest(1, hoursAgo: 2);
        AddPullRequest(2, daysAgo: 20);
        AddPullRequest(3, daysAgo: 45); // Outside the 30-day window.

        var result = await _source.FetchAsync(Fetch("pull_requests", cursor: null));

        Assert.Equal([1, 2], result.Events.OfType<PullRequestObserved>().Select(e => e.Number).Order().ToList());
    }

    [Fact]
    public async Task Incremental_pass_with_no_changes_is_cheap_and_keeps_the_watermark()
    {
        AddPullRequest(1, hoursAgo: 48);
        var first = await _source.FetchAsync(Fetch("pull_requests", cursor: null));

        // Second pass from the stored watermark: the same unchanged PR falls
        // inside the overlap window but produces no NEW material beyond a
        // redelivery, and the watermark does not move backwards or skip ahead.
        var second = await _source.FetchAsync(Fetch("pull_requests", cursor: first.NextCursor));

        Assert.Equal(first.NextCursor, second.NextCursor);

        // A brand-new PR advances both content and watermark.
        AddPullRequest(2, hoursAgo: 1);
        var third = await _source.FetchAsync(Fetch("pull_requests", cursor: second.NextCursor));
        Assert.Contains(third.Events.OfType<PullRequestObserved>(), e => e.Number == 2);
        Assert.NotEqual(second.NextCursor, third.NextCursor);
    }

    [Fact]
    public async Task Page_cap_hit_does_not_advance_the_cursor()
    {
        _options.MaxPagesPerFetch = 1;
        for (var i = 1; i <= 5; i++)
            AddPullRequest(i, hoursAgo: i); // Three pages at page size 2.

        var result = await _source.FetchAsync(Fetch("pull_requests", cursor: null));

        // Truncated walk must not produce a watermark that would skip data.
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task Rerun_enumerates_every_attempt_and_attaches_artifacts_to_the_highest()
    {
        // The runs listing exposes only the latest attempt (attempt 2 here);
        // the attempts endpoint enumerates the full rerun history.
        World.AddRun(new FakeRun
        {
            Id = 98122,
            HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RunAttempt = 2,
            Status = "completed",
            Conclusion = "success",
            CreatedAt = Now.AddDays(-1), // Reruns keep their created_at.
            UpdatedAt = Now,
            RunStartedAt = Now,
        });
        World.AddRunAttempt(new FakeRun
        {
            Id = 98122,
            HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RunAttempt = 1,
            Status = "completed",
            Conclusion = "failure",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
            RunStartedAt = Now.AddDays(-1),
        });
        World.AddRunAttempt(new FakeRun
        {
            Id = 98122,
            HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            RunAttempt = 2,
            Status = "completed",
            Conclusion = "success",
            CreatedAt = Now.AddDays(-1),
            UpdatedAt = Now,
            RunStartedAt = Now,
        });
        World.Artifacts[98122] =
        [
            new FakeArtifact { Id = 5001, Name = "test-results", CreatedAt = Now, UpdatedAt = Now },
        ];

        var result = await _source.FetchAsync(Fetch("workflow_runs", cursor: null));

        var runEvents = result.Events.OfType<WorkflowRunObserved>().OrderBy(e => e.RunAttempt).ToList();
        Assert.Equal(2, runEvents.Count);
        Assert.Equal([1, 2], runEvents.Select(e => e.RunAttempt));
        Assert.Equal("failure", runEvents[0].Conclusion);
        Assert.Equal("success", runEvents[1].Conclusion);
        Assert.All(runEvents, e => Assert.Equal(98122, e.RunId));
        // Attempt 1 keeps its historical identity; artifacts attach once.
        Assert.Empty(runEvents[0].ProducedArtifacts);
        Assert.Single(runEvents[1].ProducedArtifacts);
        Assert.Contains("acme/player-manager/actions/artifacts/5001", runEvents[1].ProducedArtifacts[0].CanonicalKeyHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_stream_applies_since_filter_and_reports_watermark()
    {
        var sha1 = "1111111111111111111111111111111111111111";
        var sha2 = "2222222222222222222222222222222222222222";
        World.Commits.Add(new FakeCommit
        {
            Sha = sha1,
            CommitterDate = Now.AddDays(-10),
            AuthorDate = Now.AddDays(-10),
        });
        World.Commits.Add(new FakeCommit
        {
            Sha = sha2,
            CommitterDate = Now.AddHours(-1),
            AuthorDate = Now.AddHours(-1),
        });

        var initial = await _source.FetchAsync(Fetch("commits", cursor: null));
        Assert.Equal(2, initial.Events.Count);

        var incremental = await _source.FetchAsync(Fetch("commits", cursor: initial.NextCursor));
        // Only commits inside watermark-minus-overlap are re-listed; the old
        // commit (10 days) sits behind the 7-day overlap floor.
        var shas = incremental.Events.OfType<CommitObserved>().Select(e => e.Sha).ToList();
        Assert.DoesNotContain(sha1, shas);
        Assert.Contains(sha2, shas);
    }

    [Fact]
    public async Task Commit_identity_keys_are_repository_scoped()
    {
        var sha = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        World.Commits.Add(new FakeCommit { Sha = sha, CommitterDate = Now.AddHours(-1), AuthorDate = Now.AddHours(-1) });

        var result = await _source.FetchAsync(Fetch("commits", cursor: null));

        var evt = Assert.Single(result.Events.OfType<CommitObserved>());
        Assert.Equal($"acme/player-manager@{sha}", evt.Provenance.ExternalKey);
        Assert.Equal("acme/player-manager", evt.Repository);
    }

    private static ResourceFetchRequest Fetch(string resourceType, string? cursor) =>
        new("acme/player-manager", resourceType, cursor, InitialLookbackDays: 30, Now);

    private void AddPullRequest(int number, int? daysAgo = null, int? hoursAgo = null)
    {
        var updatedAt = Now - (daysAgo is not null ? TimeSpan.FromDays(daysAgo.Value) : TimeSpan.FromHours(hoursAgo!.Value));
        var sha = $"{number:d40}";
        World.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR {number}",
                CreatedAt = updatedAt - TimeSpan.FromDays(1),
                UpdatedAt = updatedAt,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = updatedAt, CommitterDate = updatedAt }]);
    }

    private sealed class TestHolder(GitHubConnectorOptions options) : GitHubRepositorySyncSource.IOptionsMonitorHolder
    {
        public GitHubConnectorOptions Current => options;
    }
}
