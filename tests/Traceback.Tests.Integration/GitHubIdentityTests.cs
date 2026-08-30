using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Infrastructure.Persistence;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Repository-scoped identity: the same external identifiers in different
/// repositories are distinct engineering objects, and repeated synchronization
/// converges on exactly one row per external object.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class GitHubIdentityTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Same_pr_number_in_two_repositories_does_not_collide()
    {
        var world = NewWorld(number: 42);
        var other = OtherWorld(number: 42);

        await using var app = await StartWithWorlds(postgres.Container, world, other);
        var first = AssertSynced(await SyncAllAsync(app, "acme/player-manager"));
        var second = AssertSynced(await SyncAllAsync(app, "other-org/other-repo"));

        // Two repositories, same PR number 42: two distinct pull requests.
        Assert.Equal(1, await CountPrsAsync(app, "acme/player-manager", 42));
        Assert.Equal(1, await CountPrsAsync(app, "other-org/other-repo", 42));
    }

    [Fact]
    public async Task Same_commit_sha_and_run_id_in_two_repositories_do_not_collide()
    {
        const string sha = "cafecafecafecafecafecafecafecafecafecafe";
        var world = GitHubSyncHarness.NewWorld();
        world.AddRun(new FakeRun
        {
            Id = 777,
            HeadSha = sha,
            CreatedAt = TestTimes.Old,
            UpdatedAt = TestTimes.Old,
            RunStartedAt = TestTimes.Old,
        });
        world.Commits.Add(new FakeCommit { Sha = sha, AuthorDate = TestTimes.Older, CommitterDate = TestTimes.Older });

        var other = new FakeGitHubRepository { Owner = "other-org", Name = "other-repo" };
        other.AddRun(new FakeRun
        {
            Id = 777,
            HeadSha = sha,
            CreatedAt = TestTimes.Old,
            UpdatedAt = TestTimes.Old,
            RunStartedAt = TestTimes.Old,
        });
        other.Commits.Add(new FakeCommit { Sha = sha, AuthorDate = TestTimes.Older, CommitterDate = TestTimes.Older });

        await using var app = await StartWithWorlds(postgres.Container, world, other);
        AssertSynced(await SyncAllAsync(app));
        AssertSynced(await SyncAllAsync(app, "other-org/other-repo"));

        // Fork-style scenario: the same SHA exists in two repositories and must
        // stay independent so relationships never leak across repositories.
        Assert.Equal(2, await GitHubSyncHarness.CountRowsAsync(app, "commits"));
        Assert.Equal(2, await GitHubSyncHarness.CountRowsAsync(app, "workflow_runs"));
    }

    [Fact]
    public async Task Repeated_synchronization_resolves_the_same_external_objects()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 42);

        await using var app = await StartWithWorlds(postgres.Container, world);

        var first = AssertSynced(await SyncAllAsync(app));
        var prsAfterFirst = await GitHubSyncHarness.CountRowsAsync(app, "pull_requests");
        var observationsAfterFirst = await GitHubSyncHarness.CountRowsAsync(app, "observations");

        var second = AssertSynced(await SyncAllAsync(app));

        Assert.Equal(prsAfterFirst, await GitHubSyncHarness.CountRowsAsync(app, "pull_requests"));
        // The overlap window redelivers unchanged objects; idempotency absorbs them.
        Assert.Equal(0, second.TotalObservationsApplied);
        Assert.True(second.TotalDuplicates > 0);
        // Observation log does not grow on identical deliveries.
        Assert.Equal(observationsAfterFirst, await GitHubSyncHarness.CountRowsAsync(app, "observations"));
    }

    [Fact]
    public async Task Artifact_with_only_a_canonical_key_has_provider_evidence_and_is_idempotent()
    {
        var world = GitHubSyncHarness.NewWorld();
        world.AddRun(
            new FakeRun
            {
                Id = 778,
                HeadSha = "artifactonly000000000000000000000000000000",
                CreatedAt = TestTimes.Old,
                UpdatedAt = TestTimes.Old,
                RunStartedAt = TestTimes.Old,
            },
            [new FakeArtifact { Id = 7001, Name = "test-results" }]);

        await using var app = await StartWithWorlds(postgres.Container, world);

        AssertSynced(await SyncAllAsync(app));
        Assert.Equal(
            ["github|acme/player-manager/actions/artifacts/7001"],
            await ArtifactIdentityKeysAsync(app));

        var second = AssertSynced(await SyncAllAsync(app));

        Assert.Equal(0, second.TotalObservationsApplied);
        Assert.Equal(
            ["github|acme/player-manager/actions/artifacts/7001"],
            await ArtifactIdentityKeysAsync(app));
    }

    [Fact]
    public async Task Artifact_digest_and_canonical_key_are_preserved_as_provider_aliases()
    {
        const string digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var world = GitHubSyncHarness.NewWorld();
        world.AddRun(
            new FakeRun
            {
                Id = 779,
                HeadSha = "artifactalias00000000000000000000000000000",
                CreatedAt = TestTimes.Old,
                UpdatedAt = TestTimes.Old,
                RunStartedAt = TestTimes.Old,
            },
            [new FakeArtifact { Id = 7002, Name = "test-results", Digest = digest }]);

        await using var app = await StartWithWorlds(postgres.Container, world);

        AssertSynced(await SyncAllAsync(app));
        Assert.Equal(
            [
                $"github|acme/player-manager/actions/artifacts/7002",
                $"github|{digest}",
            ],
            await ArtifactIdentityKeysAsync(app));

        AssertSynced(await SyncAllAsync(app));
        Assert.Equal(2, (await ArtifactIdentityKeysAsync(app)).Count);
    }

    [Fact]
    public async Task Admin_sync_endpoint_triggers_configured_repository_without_exposing_secrets()
    {
        var world = GitHubSyncHarness.NewWorld();
        SeedPullRequest(world, number: 42);
        await using var app = await StartWithWorlds(postgres.Container, world);

        var response = await app.Client.PostAsync("/api/admin/integrations/github/sync/acme/player-manager", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"success\":true", body, StringComparison.Ordinal);
        Assert.Contains("pull_requests", body, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubSyncHarness.TokenSentinel, body, StringComparison.Ordinal);

        // Unconfigured repositories are rejected rather than silently synced.
        var unknown = await app.Client.PostAsync("/api/admin/integrations/github/sync/acme/unknown-repo", content: null);
        Assert.True(unknown.StatusCode == System.Net.HttpStatusCode.NotFound);
    }

    private static FakeGitHubRepository NewWorld(int number) => SeedPullRequest(GitSyncHarnessWorld(), number);

    private static FakeGitHubRepository GitSyncHarnessWorld() => GitHubSyncHarness.NewWorld();

    private static FakeGitHubRepository OtherWorld(int number)
    {
        const string sha = "42aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var other = new FakeGitHubRepository { Owner = "other-org", Name = "other-repo" };
        other.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"Other PR #{number}",
                CreatedAt = TestTimes.Old,
                UpdatedAt = TestTimes.Old,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = TestTimes.Older, CommitterDate = TestTimes.Older }]);
        return other;
    }

    private static FakeGitHubRepository SeedPullRequest(FakeGitHubRepository world, int number)
    {
        var sha = $"sha{number:d4}".PadRight(40, 'b');
        world.AddPullRequest(
            new FakePullRequest
            {
                Number = number,
                Title = $"PR #{number}",
                CreatedAt = TestTimes.Old,
                UpdatedAt = TestTimes.Old,
                HeadSha = sha,
            },
            [new FakeCommit { Sha = sha, AuthorDate = TestTimes.Older, CommitterDate = TestTimes.Older }]);
        return world;
    }

    private static Task<TracebackApp> StartWithWorlds(
        PostgreSqlContainer container, params FakeGitHubRepository[] worlds) =>
        TracebackApp.StartAsync(
            container,
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

    private static async Task<RepositorySyncResult> SyncAllAsync(TracebackApp app, string? repositoryKey = null) =>
        await GitHubSyncHarness.SyncAsync(app, repositoryKey);

    private static async Task<int> CountPrsAsync(TracebackApp app, string repositoryKey, int number)
    {
        var results = await GitHubSyncHarness.QueryAsync(app,
            "SELECT count(*) FROM pull_requests p JOIN source_repositories sr ON sr.id = p.source_repository_id " +
            "WHERE sr.key = $1 AND p.number = $2", repositoryKey, number);
        return int.Parse(results[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<List<string>> ArtifactIdentityKeysAsync(TracebackApp app) =>
        await GitHubSyncHarness.QueryAsync(
            app,
            "SELECT provider || '|' || external_key FROM external_identities " +
            "WHERE entity_type_name = 'build_artifact' ORDER BY external_key");
}

internal static class TestTimes
{
    public static readonly DateTimeOffset Older = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Old = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Nowish = new(2026, 8, 25, 11, 0, 0, TimeSpan.Zero);
}
