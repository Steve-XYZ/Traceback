using System.Net;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class DeploymentHistoryEndpointTests(PostgresContainerFixture postgres)
{
    private static string HistoryUri(string fromIso, string toIso) =>
        $"/api/services/{FixtureConnector.ServiceName}/environments/{FixtureConnector.EnvironmentName}/deployments?from={Uri.EscapeDataString(fromIso)}&to={Uri.EscapeDataString(toIso)}";

    [Fact]
    public async Task Returns_windowed_history_newest_first_with_related_context()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var uri = HistoryUri("2026-08-20T00:00:00Z", "2026-08-22T00:00:00Z");
        var result = await app.Client.GetJsonAsync(uri);

        Assert.Equal(FixtureConnector.ServiceName, result.GetProperty("serviceName").GetString());

        var deployments = result.GetProperty("deployments");
        Assert.Equal(2, deployments.GetArrayLength());

        // Newest first: today's be82d deployment, then yesterday's aa12e.
        var newest = deployments[0];
        Assert.Equal("be82d", newest.GetProperty("artifact").GetProperty("version").GetString());
        Api.AssertHasSources(newest.GetProperty("deployment"), "history[0].deployment");

        var commits = newest.GetProperty("commits");
        Assert.Equal(1, commits.GetArrayLength());
        Assert.StartsWith("be82d", commits[0].GetProperty("sha").GetString());

        var prs = newest.GetProperty("pullRequests");
        Assert.Equal(1, prs.GetArrayLength());
        Assert.Equal(1842, prs[0].GetProperty("number").GetInt32());

        var workItems = newest.GetProperty("workItems");
        Assert.Equal(1, workItems.GetArrayLength());
        Assert.Equal(FixtureConnector.WorkItemKey, workItems[0].GetProperty("key").GetString());

        // The previous build has no known ticket linkage.
        var oldest = deployments[1];
        Assert.Equal("aa12e", oldest.GetProperty("artifact").GetProperty("version").GetString());
        Assert.Equal(1, oldest.GetProperty("commits").GetArrayLength());
        Assert.Equal(0, oldest.GetProperty("pullRequests").GetArrayLength());
        Assert.Equal(0, oldest.GetProperty("workItems").GetArrayLength());
    }

    [Fact]
    public async Task Narrow_window_excludes_out_of_range_deployments()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var uri = HistoryUri("2026-08-21T00:00:00Z", "2026-08-22T00:00:00Z");
        var result = await app.Client.GetJsonAsync(uri);

        var deployments = result.GetProperty("deployments");
        Assert.Equal(1, deployments.GetArrayLength());
    }

    [Fact]
    public async Task Unknown_environment_returns_404()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var uri = $"/api/services/{FixtureConnector.ServiceName}/environments/production/deployments";
        Assert.Equal(HttpStatusCode.NotFound, await app.Client.GetStatusAsync(uri));
    }
}
