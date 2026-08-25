using System.Net;
using System.Text.Json;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

/// <summary>
/// The fixture scenario is authored newest-first. These tests prove that
/// relationship edges survive out-of-order arrival and that placeholder rows
/// created by forward references are resolved once the real observations land.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class OutOfOrderIngestionTests(PostgresContainerFixture postgres)
{
    private static async Task<List<TracebackEvent>> ScenarioAsync() =>
        await DuplicateIngestionTests.CollectAllAsync(new FixtureConnector());

    [Fact]
    public async Task Deployment_first_arrival_still_reconstructs_the_full_chain()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var events = await ScenarioAsync();
        // Sanity-check the scenario really is authored newest-first, then ingest as-is.
        int IndexOf(Func<TracebackEvent, bool> predicate) => events.FindIndex(e => predicate(e));
        var deploymentIdx = IndexOf(e => e.Provenance.EntityType == "deployment"
            && e.Provenance.ExternalKey.Contains(FixtureConnector.ArtifactTag, StringComparison.Ordinal));
        Assert.True(deploymentIdx < IndexOf(e => e.Provenance.ExternalKey == FixtureConnector.WorkflowRunName),
            "deployment must be ingested before its workflow run");
        Assert.True(deploymentIdx < events.FindIndex(e =>
                e.Provenance.EntityType == "commit" && e.Provenance.ExternalKey.Contains(FixtureConnector.ArtifactTag, StringComparison.Ordinal)),
            "deployment must be ingested before its commit");
        Assert.True(deploymentIdx < IndexOf(e => e.Provenance.ExternalKey == FixtureConnector.PullRequestName),
            "deployment must be ingested before its pull request");
        Assert.Equal(events.Count - 1, IndexOf(e => e.Provenance.EntityType == "work_item"));

        var result = await app.IngestAsync(events);

        Assert.Equal(events.Count, result.Applied);

        var chain = await app.Client.GetJsonAsync($"/api/work-items/{FixtureConnector.WorkItemKey}/deployment");

        var pr = chain.GetProperty("chains")[0].GetProperty("pullRequest");
        Assert.Equal(1842, pr.GetProperty("number").GetInt32());

        var commit = chain.GetProperty("chains")[0].GetProperty("commits")[0].GetProperty("commit");
        Assert.StartsWith(FixtureConnector.ArtifactTag, commit.GetProperty("sha").GetString());

        var artifactChain = FindArtifact(chain, FixtureConnector.ArtifactName, FixtureConnector.ArtifactTag)
            ?? throw new Xunit.Sdk.XunitException("Expected the player-manager:be82d artifact in the chain.");
        Assert.True(artifactChain.GetProperty("deployments").GetArrayLength() >= 1);

        // No placeholder may survive a complete observation set.
        Assert.False(ContainsPlaceholder(chain), "No node should remain a placeholder after all events arrived");
    }

    [Fact]
    public async Task Work_item_edge_survives_when_ticket_is_observed_last()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var events = await ScenarioAsync();
        var workItemEvent = events.Single(e => e.Provenance.EntityType == "work_item");
        var withoutTicket = events.Where(e => e != workItemEvent).ToList();

        // The ticket does not exist until its own event arrives...
        await app.IngestAsync(withoutTicket);
        Assert.Equal(HttpStatusCode.NotFound,
            await app.Client.GetStatusAsync($"/api/work-items/{FixtureConnector.WorkItemKey}/deployment"));

        // ...and when it arrives it still links to the already-known pull request.
        await app.IngestAsync([workItemEvent]);

        var chain = await app.Client.GetJsonAsync($"/api/work-items/{FixtureConnector.WorkItemKey}/deployment");
        var pr = chain.GetProperty("chains")[0].GetProperty("pullRequest");
        Assert.Equal(1842, pr.GetProperty("number").GetInt32());
    }

    [Fact]
    public async Task Referenced_before_observed_entities_are_placeholders_until_filled()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var events = await ScenarioAsync();

        // Only the newest deployment event: everything it references must materialize as placeholders.
        var deploymentEvent = events.Single(e => e.Provenance.EntityType == "deployment"
            && e.Provenance.ExternalKey.Contains(FixtureConnector.ArtifactTag, StringComparison.Ordinal));
        await app.IngestAsync([deploymentEvent]);

        var counts = await DuplicateIngestionTests.QueryCountsAsync(app,
            ["services", "environments", "build_artifacts", "workflow_runs"]);
        Assert.Equal(1, counts["services"]);
        Assert.Equal(1, counts["environments"]);
        Assert.Equal(1, counts["build_artifacts"]);
        Assert.Equal(1, counts["workflow_runs"]); // stub run from TriggeredByWorkflowRun

        // Now deliver the full scenario; the same entities absorb their details.
        await app.IngestAsync(events);
        var current = await app.Client.GetJsonAsync(
            $"/api/services/{FixtureConnector.ServiceName}/environments/{FixtureConnector.EnvironmentName}/current-deployment");

        var artifact = current.GetProperty("current").GetProperty("artifact");
        Assert.Equal(FixtureConnector.ArtifactTag, artifact.GetProperty("version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(artifact.GetProperty("digest").GetString()));
    }

    private static JsonElement? FindArtifact(JsonElement chain, string name, string version)
    {
        foreach (var prChain in chain.GetProperty("chains").EnumerateArray())
            foreach (var commitChain in prChain.GetProperty("commits").EnumerateArray())
                foreach (var runChain in commitChain.GetProperty("workflowRuns").EnumerateArray())
                    foreach (var artifactChain in runChain.GetProperty("artifacts").EnumerateArray())
                    {
                        var artifact = artifactChain.GetProperty("artifact");
                        if (artifact.GetProperty("name").GetString() == name
                            && artifact.GetProperty("version").GetString() == version)
                            return artifactChain.Clone();
                    }
        return null;
    }

    private static bool ContainsPlaceholder(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("isPlaceholder", out var flag) && flag.GetBoolean())
                    return true;
                foreach (var child in element.EnumerateObject())
                    if (ContainsPlaceholder(child.Value))
                        return true;
                return false;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                    if (ContainsPlaceholder(child))
                        return true;
                return false;
            default:
                return false;
        }
    }
}
