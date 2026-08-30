using Microsoft.Extensions.Options;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.GitHub;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.GitHub;

/// <summary>
/// Stream-level synchronization strategy: pagination without truncation,
/// initial-lookback windows, incremental watermarks, rerun attempt
/// enumeration, repeatable typed failures on truncation, and consistent
/// initial cursor reporting.
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
    public async Task Pull_request_commit_walk_succeeds_at_exact_page_cap()
    {
        _options.MaxPagesPerFetch = 2;
        AddPullRequestWithCommits(42, count: 4);

        var result = await _source.FetchAsync(Fetch("pull_requests", cursor: null));

        var pullRequest = Assert.Single(result.Events.OfType<PullRequestObserved>());
        Assert.Equal(4, pullRequest.CommitShas.Count);
        Assert.Equal(2, _handler.RequestLog.Count(path => path.Contains("/pulls/42/commits", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Pull_request_commit_walk_fails_and_repeats_when_cap_is_exceeded()
    {
        _options.MaxPagesPerFetch = 2;
        AddPullRequestWithCommits(42, count: 5);

        var first = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch("pull_requests", cursor: null)));
        var second = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch("pull_requests", cursor: null)));

        Assert.Equal("pull_request_commits", first.ResourceType);
        Assert.Equal(2, first.PagesWalked);
        Assert.Equal(2, first.MaxPages);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(4, _handler.RequestLog.Count(path => path.Contains("/pulls/42/commits", StringComparison.Ordinal)));
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

    [Theory]
    [InlineData("pull_requests")]
    [InlineData("commits")]
    [InlineData("workflow_runs")]
    public async Task Initial_page_cap_is_a_repeatable_typed_failure_for_every_stream(string resourceType)
    {
        _options.MaxPagesPerFetch = 1;
        SeedPagedStream(resourceType);

        var first = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch(resourceType, cursor: null)));
        var second = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch(resourceType, cursor: null)));

        Assert.Equal(resourceType, first.ResourceType);
        Assert.Equal(1, first.MaxPages);
        Assert.Equal(first.Message, second.Message);
    }

    [Theory]
    [InlineData("pull_requests")]
    [InlineData("commits")]
    [InlineData("workflow_runs")]
    public async Task Empty_initial_windows_report_no_cursor_for_every_stream(string resourceType)
    {
        var result = await _source.FetchAsync(Fetch(resourceType, cursor: null));

        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task Initial_commit_and_workflow_passes_exclude_overlap_only_items()
    {
        var oldSha = "3333333333333333333333333333333333333333";
        var oldAt = Now.AddDays(-35); // In the incremental overlap, outside the initial window.
        World.Commits.Add(new FakeCommit
        {
            Sha = oldSha,
            AuthorDate = oldAt,
            CommitterDate = oldAt,
        });
        World.AddRun(new FakeRun
        {
            Id = 8301,
            HeadSha = oldSha,
            CreatedAt = oldAt,
            UpdatedAt = oldAt,
            RunStartedAt = oldAt,
        });

        var commits = await _source.FetchAsync(Fetch("commits", cursor: null));
        var workflows = await _source.FetchAsync(Fetch("workflow_runs", cursor: null));

        Assert.Empty(commits.Events.OfType<CommitObserved>());
        Assert.Null(commits.NextCursor);
        Assert.Empty(workflows.Events.OfType<WorkflowRunObserved>());
        Assert.Null(workflows.NextCursor);
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

    [Fact]
    public async Task Artifacts_come_from_the_repository_listing_when_that_costs_fewer_requests()
    {
        // Six runs, one artifact each: the repository-wide listing needs
        // ceil(6/2) = 3 pages against 6 per-run requests, so it wins.
        for (var i = 1; i <= 6; i++)
        {
            World.AddRun(
                new FakeRun
                {
                    Id = 500 + i,
                    HeadSha = $"sha{i:d4}".PadRight(40, 'a'),
                    CreatedAt = Now.AddHours(-i),
                    RunStartedAt = Now.AddHours(-i),
                    UpdatedAt = Now.AddHours(-i),
                },
                [new FakeArtifact { Id = 700 + i, Name = $"drop-{i}" }]);
        }

        var result = await _source.FetchAsync(Fetch("workflow_runs", cursor: null));

        var runEvents = result.Events.OfType<WorkflowRunObserved>().ToList();
        Assert.Equal(6, runEvents.Count);
        Assert.All(runEvents, e => Assert.Single(e.ProducedArtifacts));
        Assert.Equal(
            ["drop-1", "drop-2", "drop-3", "drop-4", "drop-5", "drop-6"],
            runEvents.SelectMany(e => e.ProducedArtifacts).Select(a => a.Name).Order().ToList());

        // The repository-wide listing was walked; no per-run artifact request.
        Assert.Contains(_handler.RequestLog, r => r.Contains("/actions/artifacts", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestLog, r => r.Contains("/runs/501/artifacts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Artifacts_fall_back_to_per_run_requests_when_the_repository_listing_is_larger()
    {
        // Two runs in the window but ten artifacts in the repository: the
        // repository listing would need five pages, so per-run wins.
        for (var i = 1; i <= 2; i++)
        {
            World.AddRun(
                new FakeRun
                {
                    Id = 600 + i,
                    HeadSha = $"sha{i:d4}".PadRight(40, 'c'),
                    CreatedAt = Now.AddHours(-i),
                    RunStartedAt = Now.AddHours(-i),
                    UpdatedAt = Now.AddHours(-i),
                },
                [new FakeArtifact { Id = 800 + i, Name = $"drop-{i}" }]);
        }
        // Artifacts belonging to runs outside the window inflate the listing.
        World.Artifacts[999] = [.. Enumerable.Range(1, 8).Select(i => new FakeArtifact { Id = 900 + i, Name = $"old-{i}" })];

        var result = await _source.FetchAsync(Fetch("workflow_runs", cursor: null));

        var runEvents = result.Events.OfType<WorkflowRunObserved>().ToList();
        Assert.Equal(2, runEvents.Count);
        Assert.All(runEvents, e => Assert.Single(e.ProducedArtifacts));
        Assert.Contains(_handler.RequestLog, r => r.Contains("/runs/601/artifacts", StringComparison.Ordinal));
        Assert.Contains(_handler.RequestLog, r => r.Contains("/runs/602/artifacts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Per_run_artifact_walk_succeeds_at_exact_page_cap()
    {
        _options.MaxPagesPerFetch = 2;
        AddRunWithArtifacts(7001, count: 4);

        var result = await _source.FetchAsync(Fetch("workflow_runs", cursor: null));

        var run = Assert.Single(result.Events.OfType<WorkflowRunObserved>());
        Assert.Equal(4, run.ProducedArtifacts.Count);
        Assert.Equal(2, _handler.RequestLog.Count(path => path.Contains("/actions/runs/7001/artifacts", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Per_run_artifact_walk_fails_and_repeats_when_cap_is_exceeded()
    {
        _options.MaxPagesPerFetch = 2;
        AddRunWithArtifacts(7001, count: 5);

        var first = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch("workflow_runs", cursor: null)));
        var second = await Assert.ThrowsAsync<GitHubPageLimitException>(
            () => _source.FetchAsync(Fetch("workflow_runs", cursor: null)));

        Assert.Equal("workflow_run_artifacts", first.ResourceType);
        Assert.Equal(2, first.PagesWalked);
        Assert.Equal(2, first.MaxPages);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(4, _handler.RequestLog.Count(path => path.Contains("/actions/runs/7001/artifacts", StringComparison.Ordinal)));
    }

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

    private void AddPullRequestWithCommits(int number, int count)
    {
        var updatedAt = Now.AddHours(-1);
        var commits = Enumerable.Range(1, count)
            .Select(i => new FakeCommit
            {
                Sha = $"pr{number:d4}commit{i:d2}".PadRight(40, 'a'),
                AuthorDate = updatedAt,
                CommitterDate = updatedAt,
            })
            .ToList();
        World.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR {number}",
                CreatedAt = updatedAt.AddHours(-1),
                UpdatedAt = updatedAt,
                HeadSha = commits[^1].Sha,
            },
            commits);
    }

    private void AddRunWithArtifacts(long runId, int count)
    {
        var createdAt = Now.AddHours(-1);
        World.AddRun(
            new FakeRun
            {
                Id = runId,
                HeadSha = $"run{runId}sha".PadRight(40, 'b'),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                RunStartedAt = createdAt,
            },
            Enumerable.Range(1, count)
                .Select(i => new FakeArtifact { Id = runId * 10 + i, Name = $"drop-{i}" })
                .ToList());
    }

    private void SeedPagedStream(string resourceType)
    {
        switch (resourceType)
        {
            case "pull_requests":
                for (var i = 1; i <= 5; i++)
                    AddPullRequest(i, hoursAgo: i);
                break;
            case "commits":
                for (var i = 1; i <= 5; i++)
                {
                    var sha = $"capcommit{i:d2}".PadRight(40, 'a');
                    World.Commits.Add(new FakeCommit
                    {
                        Sha = sha,
                        AuthorDate = Now.AddHours(-i),
                        CommitterDate = Now.AddHours(-i),
                    });
                }
                break;
            case "workflow_runs":
                for (var i = 1; i <= 5; i++)
                {
                    World.AddRun(new FakeRun
                    {
                        Id = 700 + i,
                        HeadSha = $"caprun{i:d2}".PadRight(40, 'b'),
                        CreatedAt = Now.AddHours(-i),
                        UpdatedAt = Now.AddHours(-i),
                        RunStartedAt = Now.AddHours(-i),
                    });
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
        }
    }

    private sealed class TestHolder(GitHubConnectorOptions options) : GitHubRepositorySyncSource.IOptionsMonitorHolder
    {
        public GitHubConnectorOptions Current => options;
    }
}
