using System.Net;
using System.Text.Json;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class CurrentDeploymentEndpointTests(PostgresContainerFixture postgres)
{
    private static readonly string CurrentUri =
        $"/api/services/{FixtureConnector.ServiceName}/environments/{FixtureConnector.EnvironmentName}/current-deployment";

    [Fact]
    public async Task Reports_newest_successful_deployment_and_resolved_revision()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var result = await app.Client.GetJsonAsync(CurrentUri);

        Assert.Equal(FixtureConnector.ServiceName, result.GetProperty("serviceName").GetString());
        Assert.Equal(FixtureConnector.EnvironmentName, result.GetProperty("environmentName").GetString());

        var current = result.GetProperty("current");
        var deployment = current.GetProperty("deployment");
        Assert.Equal("succeeded", deployment.GetProperty("status").GetString());
        Api.AssertHasSources(deployment, "current.deployment");

        var artifact = current.GetProperty("artifact");
        Assert.Equal(FixtureConnector.ArtifactName, artifact.GetProperty("name").GetString());
        Assert.Equal(FixtureConnector.ArtifactTag, artifact.GetProperty("version").GetString());

        var revision = current.GetProperty("revision");
        Assert.StartsWith(FixtureConnector.ArtifactTag, revision.GetProperty("sha").GetString());
        Assert.Contains("#1842", revision.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Superseded_deployments_are_not_current()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var result = await app.Client.GetJsonAsync(CurrentUri);

        var version = result.GetProperty("current").GetProperty("artifact").GetProperty("version").GetString();
        var previousTag = "aa12e";
        Assert.NotEqual(previousTag, version);
    }

    [Fact]
    public async Task Unknown_service_returns_404()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            await app.Client.GetStatusAsync("/api/services/nope/environments/staging/current-deployment"));
    }

    [Fact]
    public async Task Known_service_without_deployments_reports_null_current()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);

        // Service and environment observed but never deployed.
        var events = await DuplicateIngestionTests.CollectAllAsync(new FixtureConnector());
        await app.IngestAsync(events.Where(e => e.Provenance.EntityType is "service" or "environment"));

        var result = await app.Client.GetJsonAsync(CurrentUri);

        Assert.True(result.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Null,
            "current must be null when no successful deployment exists");
    }
}
