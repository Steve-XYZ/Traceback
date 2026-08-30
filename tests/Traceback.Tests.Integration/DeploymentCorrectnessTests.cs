using Npgsql;
using Traceback.Connectors.Abstractions;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class DeploymentCorrectnessTests(PostgresContainerFixture postgres)
{
    private static readonly ArtifactDescriptor Artifact = new(
        "checkout", "v1", "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null);

    [Fact]
    public async Task Same_artifact_redeployments_at_different_times_keep_distinct_rows_and_identities()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var firstDeployedAt = new DateTimeOffset(2026, 08, 20, 10, 00, 00, TimeSpan.Zero);
        var secondDeployedAt = firstDeployedAt.AddHours(2);

        var result = await app.IngestAsync(
        [
            Deployment("docker", "checkout/staging", Artifact, DeploymentOutcome.Succeeded,
                firstDeployedAt, firstDeployedAt, firstDeployedAt.AddMinutes(1)),
            Deployment("docker", "checkout/staging", Artifact, DeploymentOutcome.Succeeded,
                secondDeployedAt, secondDeployedAt, secondDeployedAt.AddMinutes(1)),
        ]);

        Assert.Equal(2, result.Applied);
        Assert.Equal(2, await CountAsync(app, "deployments"));
        Assert.Equal(2, await CountDeploymentIdentitiesAsync(app));

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal(2, history.GetProperty("deployments").GetArrayLength());
        foreach (var entry in history.GetProperty("deployments").EnumerateArray())
        {
            Assert.Contains(entry.GetProperty("deployment").GetProperty("sources").EnumerateArray(), source =>
            {
                return source.GetProperty("provider").GetString() == "docker"
                    && source.GetProperty("externalKey").GetString() == "checkout/staging";
            });
        }
    }

    [Theory]
    [InlineData("succeeded", "failed")]
    [InlineData("failed", "succeeded")]
    public async Task Lifecycle_updates_are_freshness_ordered_and_terminal_state_is_preserved(
        string terminalStatus,
        string staleStatus)
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 11, 00, 00, TimeSpan.Zero);
        var startedAt = deployedAt;
        var completedAt = deployedAt.AddMinutes(5);
        var terminalOutcome = new DeploymentOutcome(terminalStatus);
        var staleOutcome = new DeploymentOutcome(staleStatus);

        await app.IngestAsync([Deployment("docker", "checkout/deployment-1", Artifact, DeploymentOutcome.InProgress,
            deployedAt, startedAt, startedAt.AddMinutes(1))]);
        await app.IngestAsync([Deployment("docker", "checkout/deployment-1", Artifact, terminalOutcome,
            deployedAt, completedAt, completedAt.AddMinutes(1))]);
        await app.IngestAsync([Deployment("docker", "checkout/deployment-1", Artifact, staleOutcome,
            deployedAt, startedAt.AddSeconds(30), completedAt.AddMinutes(2))]);

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal(terminalStatus, history.GetProperty("deployments")[0].GetProperty("deployment").GetProperty("status").GetString());
        Assert.Equal(1, await CountAsync(app, "deployments"));

        if (terminalStatus == "succeeded")
        {
            var current = await app.Client.GetJsonAsync(
                "/api/services/checkout/environments/staging/current-deployment");
            Assert.Equal("succeeded", current.GetProperty("current").GetProperty("deployment").GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task Statusless_observation_does_not_watermark_a_later_older_terminal_update()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 11, 00, 00, TimeSpan.Zero);

        await app.IngestAsync([Deployment("docker", "checkout/deployment-statusless-first", Artifact, null,
            deployedAt, deployedAt.AddMinutes(10), deployedAt.AddMinutes(11))]);
        await app.IngestAsync([Deployment("docker", "checkout/deployment-statusless-first", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt.AddMinutes(5), deployedAt.AddMinutes(12))]);

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal("succeeded", history.GetProperty("deployments")[0].GetProperty("deployment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Statusless_observation_does_not_block_terminal_progress_after_nonterminal_state()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 11, 00, 00, TimeSpan.Zero);

        await app.IngestAsync([Deployment("docker", "checkout/deployment-statusless-middle", Artifact, DeploymentOutcome.InProgress,
            deployedAt, deployedAt, deployedAt.AddMinutes(1))]);
        await app.IngestAsync([Deployment("docker", "checkout/deployment-statusless-middle", Artifact, null,
            deployedAt, deployedAt.AddMinutes(10), deployedAt.AddMinutes(11))]);
        await app.IngestAsync([Deployment("docker", "checkout/deployment-statusless-middle", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt.AddMinutes(5), deployedAt.AddMinutes(12))]);

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal("succeeded", history.GetProperty("deployments")[0].GetProperty("deployment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_second_provider_adds_deployment_evidence_to_the_existing_natural_deployment()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 12, 00, 00, TimeSpan.Zero);

        await app.IngestAsync(
        [
            Deployment("docker", "checkout/docker-rollout-1", Artifact, DeploymentOutcome.Succeeded,
                deployedAt, deployedAt, deployedAt.AddMinutes(1)),
            Deployment("argocd", "checkout/argocd-rollout-7", Artifact, DeploymentOutcome.Succeeded,
                deployedAt, deployedAt.AddSeconds(10), deployedAt.AddMinutes(2)),
        ]);

        var current = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/current-deployment");
        var sources = current.GetProperty("current").GetProperty("deployment").GetProperty("sources")
            .EnumerateArray()
            .Select(source => $"{source.GetProperty("provider").GetString()}/{source.GetProperty("externalKey").GetString()}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1, await CountAsync(app, "deployments"));
        Assert.Equal(2, await CountDeploymentIdentitiesAsync(app));
        Assert.Contains("docker/checkout/docker-rollout-1", sources);
        Assert.Contains("argocd/checkout/argocd-rollout-7", sources);
    }

    [Fact]
    public async Task A_new_linked_provider_observation_does_not_hide_legacy_deployment_evidence()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 12, 30, 00, TimeSpan.Zero);

        await app.IngestAsync([Deployment("docker", "checkout/legacy-rollout", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt, deployedAt.AddMinutes(1))]);
        // Simulate a deployment observation written before the evidence link
        // migration. Its synthetic ExternalIdentity remains durable fallback evidence.
        await ExecuteAsync(app, "UPDATE observations SET deployment_id = NULL WHERE entity_type_name = 'deployment' AND external_key = 'checkout/legacy-rollout'");

        await app.IngestAsync([Deployment("argocd", "checkout/new-provider-rollout", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt.AddSeconds(10), deployedAt.AddMinutes(2))]);

        var current = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/current-deployment");
        var sources = current.GetProperty("current").GetProperty("deployment").GetProperty("sources")
            .EnumerateArray()
            .Select(source =>
                (Provider: source.GetProperty("provider").GetString()!,
                    ExternalKey: source.GetProperty("externalKey").GetString()!))
            .ToList();

        Assert.Equal(sources.Count, sources.Distinct().Count());
        Assert.Equal(
            sources.OrderBy(source => source.Provider, StringComparer.Ordinal)
                .ThenBy(source => source.ExternalKey, StringComparer.Ordinal),
            sources);
        Assert.Contains(sources, source => source.Provider == "docker"
            && source.ExternalKey.StartsWith("deployments/", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Provider == "argocd"
            && source.ExternalKey == "checkout/new-provider-rollout");
    }

    [Fact]
    public async Task A_second_provider_cannot_overwrite_the_canonical_provider_status_clock()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 12, 00, 00, TimeSpan.Zero);

        await app.IngestAsync([Deployment("docker", "checkout/provider-status", Artifact, DeploymentOutcome.InProgress,
            deployedAt, deployedAt, deployedAt.AddMinutes(1))]);
        await app.IngestAsync([Deployment("argocd", "checkout/provider-status", Artifact, DeploymentOutcome.Failed,
            deployedAt, deployedAt.AddHours(1), deployedAt.AddHours(1).AddMinutes(1))]);
        await app.IngestAsync([Deployment("docker", "checkout/provider-status", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt.AddMinutes(30), deployedAt.AddHours(1).AddMinutes(2))]);

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal("succeeded", history.GetProperty("deployments")[0].GetProperty("deployment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Legacy_terminal_deployment_without_watermark_cannot_be_regressed()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 15, 00, 00, TimeSpan.Zero);

        await app.IngestAsync([Deployment("docker", "checkout/legacy-terminal", Artifact, DeploymentOutcome.Succeeded,
            deployedAt, deployedAt, deployedAt.AddMinutes(1))]);
        // Simulate a row created before DeploymentLifecycleFreshness: the
        // additive migration leaves its source-state watermark null.
        await ExecuteAsync(app, "UPDATE deployments SET provider_state_at = NULL");

        await app.IngestAsync([Deployment("docker", "checkout/legacy-terminal", Artifact, DeploymentOutcome.Failed,
            deployedAt, deployedAt.AddMinutes(-10), deployedAt.AddMinutes(2))]);

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        Assert.Equal("succeeded", history.GetProperty("deployments")[0].GetProperty("deployment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Explicit_deployment_run_wins_over_a_newer_run_for_current_and_history_revision()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 13, 00, 00, TimeSpan.Zero);
        var oldCommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var newCommitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var oldRunCompletedAt = deployedAt.AddMinutes(-20);
        var newRunCompletedAt = deployedAt.AddMinutes(-10);

        await app.IngestAsync(
        [
            Deployment("docker", "checkout/deployment-1", Artifact, DeploymentOutcome.Succeeded,
                deployedAt, deployedAt, deployedAt.AddMinutes(1),
                new ExternalRef("github", "workflow_run", "checkout/run-old")),
            WorkflowRun("checkout/run-new", newCommitSha, Artifact, newRunCompletedAt, newRunCompletedAt.AddMinutes(1), 2),
            WorkflowRun("checkout/run-old", oldCommitSha, Artifact, oldRunCompletedAt, oldRunCompletedAt.AddMinutes(1), 1),
        ]);

        var current = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/current-deployment");
        Assert.Equal(oldCommitSha, current.GetProperty("current").GetProperty("revision").GetProperty("sha").GetString());

        var history = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/deployments?from=2026-08-20T00:00:00Z&to=2026-08-21T00:00:00Z");
        var commits = history.GetProperty("deployments")[0].GetProperty("commits");
        Assert.Single(commits.EnumerateArray());
        Assert.Equal(oldCommitSha, commits[0].GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Deployment_without_explicit_run_uses_the_newest_artifact_producer_as_fallback()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 14, 00, 00, TimeSpan.Zero);
        var oldCommitSha = "cccccccccccccccccccccccccccccccccccc";
        var newCommitSha = "dddddddddddddddddddddddddddddddddddd";
        var oldRunCompletedAt = deployedAt.AddMinutes(-20);
        var newRunCompletedAt = deployedAt.AddMinutes(-10);

        await app.IngestAsync(
        [
            Deployment("docker", "checkout/deployment-2", Artifact, DeploymentOutcome.Succeeded,
                deployedAt, deployedAt, deployedAt.AddMinutes(1)),
            WorkflowRun("checkout/run-old", oldCommitSha, Artifact, oldRunCompletedAt, oldRunCompletedAt.AddMinutes(1), 1),
            WorkflowRun("checkout/run-new", newCommitSha, Artifact, newRunCompletedAt, newRunCompletedAt.AddMinutes(1), 2),
        ]);

        var current = await app.Client.GetJsonAsync(
            "/api/services/checkout/environments/staging/current-deployment");
        Assert.Equal(newCommitSha, current.GetProperty("current").GetProperty("revision").GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Work_item_chain_includes_explicit_deployment_when_run_has_no_artifact_edge()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var deployedAt = new DateTimeOffset(2026, 08, 20, 16, 00, 00, TimeSpan.Zero);
        var commitSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var pullRequestKey = "checkout/pr-42";
        var runKey = "checkout/run-explicit-no-artifact";
        var workItemKey = "BOS-0042";

        await app.IngestAsync(
        [
            new WorkItemObserved(
                new EventProvenance("linear", "work_item", workItemKey, null, deployedAt, deployedAt),
                workItemKey,
                "Deploy checkout",
                null,
                "open",
                "task",
                null,
                null,
                [new ExternalRef("github", "pull_request", pullRequestKey)]),
            new PullRequestObserved(
                new EventProvenance("github", "pull_request", pullRequestKey, null, deployedAt, deployedAt),
                pullRequestKey,
                "checkout",
                42,
                "Deploy checkout",
                "open",
                null,
                null,
                null,
                [commitSha],
                UpdatedAt: deployedAt),
            new CommitObserved(
                new EventProvenance("github", "commit", $"checkout@{commitSha}", null, deployedAt, deployedAt),
                commitSha,
                "checkout",
                "Deploy checkout",
                deployedAt,
                null,
                deployedAt,
                null),
            Deployment("docker", "checkout/deployment-no-artifact-edge", Artifact, DeploymentOutcome.Succeeded,
                deployedAt, deployedAt, deployedAt.AddMinutes(1),
                new ExternalRef("github", "workflow_run", runKey)),
            WorkflowRun(runKey, commitSha, Artifact, deployedAt.AddMinutes(-2), deployedAt, 42, [], "checkout"),
        ]);

        Assert.Equal(0, await CountAsync(app, "workflow_run_artifacts"));
        var chain = await app.Client.GetJsonAsync($"/api/work-items/{workItemKey}/deployment");
        var deployments = chain.GetProperty("chains")[0]
            .GetProperty("commits")[0]
            .GetProperty("workflowRuns")[0]
            .GetProperty("artifacts")[0]
            .GetProperty("deployments");

        Assert.Contains(deployments.EnumerateArray(), deployment =>
            deployment.GetProperty("status").GetString() == "succeeded");
    }

    private static DeploymentObserved Deployment(
        string provider,
        string externalKey,
        ArtifactDescriptor artifact,
        DeploymentOutcome? outcome,
        DateTimeOffset deployedAt,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        ExternalRef? workflowRun = null) =>
        new(
            new EventProvenance(provider, "deployment", externalKey, null, occurredAt, observedAt),
            "checkout",
            "staging",
            artifact,
            outcome,
            deployedAt,
            workflowRun);

    private static WorkflowRunObserved WorkflowRun(
        string externalName,
        string commitSha,
        ArtifactDescriptor artifact,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long runNumber,
        IReadOnlyList<ArtifactDescriptor>? producedArtifacts = null,
        string? repository = null) =>
        new(
            new EventProvenance("github", "workflow_run", externalName, null, completedAt, completedAt.AddMinutes(2)),
            externalName,
            "checkout-ci",
            runNumber,
            "completed",
            "success",
            startedAt,
            completedAt,
            commitSha,
            producedArtifacts ?? [artifact],
            Repository: repository,
            UpdatedAt: completedAt);

    private static async Task<int> CountAsync(TracebackApp app, string table)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountDeploymentIdentitiesAsync(TracebackApp app)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM external_identities WHERE entity_type_name = 'deployment'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(TracebackApp app, string sql)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
