using Traceback.Connectors.Abstractions;
using System.Net;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

/// <summary>Acceptance path: BOS-2268 → PR #1842 → commit be82d… → run #98122 → player-manager:be82d → staging.</summary>
[Collection(PostgresTestCollection.Name)]
public sealed class WorkItemDeploymentEndpointTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Returns_the_complete_chain_with_provenance()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var chain = await app.Client.GetJsonAsync($"/api/work-items/{FixtureConnector.WorkItemKey}/deployment");

        var workItem = chain.GetProperty("workItem");
        Assert.Equal(FixtureConnector.WorkItemKey, workItem.GetProperty("key").GetString());
        Api.AssertHasSources(workItem, "workItem");

        var chains = chain.GetProperty("chains");
        Assert.Equal(1, chains.GetArrayLength());
        var prChain = chains[0];

        var pr = prChain.GetProperty("pullRequest");
        Assert.Equal(FixtureConnector.PullRequestName, pr.GetProperty("externalName").GetString());
        Assert.Equal(1842, pr.GetProperty("number").GetInt32());
        Assert.Equal("merged", pr.GetProperty("state").GetString());
        Api.AssertHasSources(pr, "pullRequest");

        var commits = prChain.GetProperty("commits");
        Assert.Equal(1, commits.GetArrayLength());
        var commit = commits[0].GetProperty("commit");
        Assert.StartsWith(FixtureConnector.ArtifactTag, commit.GetProperty("sha").GetString());
        Api.AssertHasSources(commit, "commit");

        var runs = commits[0].GetProperty("workflowRuns");
        Assert.Equal(1, runs.GetArrayLength());
        var run = runs[0].GetProperty("workflowRun");
        Assert.Equal(FixtureConnector.WorkflowRunName, run.GetProperty("externalName").GetString());
        Assert.Equal(98122, run.GetProperty("runNumber").GetInt64());
        Assert.Equal("success", run.GetProperty("conclusion").GetString());

        var artifacts = runs[0].GetProperty("artifacts");
        Assert.Equal(1, artifacts.GetArrayLength());
        var artifact = artifacts[0].GetProperty("artifact");
        Assert.Equal(FixtureConnector.ArtifactName, artifact.GetProperty("name").GetString());
        Assert.Equal(FixtureConnector.ArtifactTag, artifact.GetProperty("version").GetString());
        Assert.NotNull(artifact.GetProperty("digest").GetString());

        var deployments = artifacts[0].GetProperty("deployments");
        Assert.True(deployments.GetArrayLength() >= 1);
        var deployment = deployments[0];
        Assert.Equal(FixtureConnector.EnvironmentName, deployment.GetProperty("environmentName").GetString());
        Assert.Equal(FixtureConnector.ServiceName, deployment.GetProperty("serviceName").GetString());
        Assert.Equal("succeeded", deployment.GetProperty("status").GetString());
        Api.AssertHasSources(deployment, "deployment");
    }

    [Fact]
    public async Task Unknown_work_item_returns_404()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            await app.Client.GetStatusAsync("/api/work-items/XXX-9999/deployment"));
    }

    [Fact]
    public async Task Work_item_with_no_pull_request_returns_empty_chains()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);

        // Ingest everything except the work item, then a ticket with no PR link.
        var events = await DuplicateIngestionTests.CollectAllAsync(new FixtureConnector());
        var ticket = events.Single(e => e.Provenance.EntityType == "work_item");
        await app.IngestAsync(events.Where(e => e != ticket));
        await app.IngestAsync([new WorkItemObserved(
            new EventProvenance("linear", "work_item", "BOS-0001", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            "BOS-0001", "Standalone ticket", null, "Todo", "bug", null, null, [])]);

        var chain = await app.Client.GetJsonAsync("/api/work-items/BOS-0001/deployment");

        Assert.Equal(0, chain.GetProperty("chains").GetArrayLength());
    }
}
