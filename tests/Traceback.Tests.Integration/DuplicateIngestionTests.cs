using Npgsql;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class DuplicateIngestionTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Ingesting_the_same_scenario_twice_creates_no_duplicates()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);

        var first = await app.IngestScenarioAsync();
        Assert.True(first.Applied > 0);

        var second = await app.IngestScenarioAsync();

        Assert.Equal(0, second.Applied);
        Assert.Equal(first.Applied, second.Duplicated);

        var counts = await QueryCountsAsync(app,
        [
            "work_items", "pull_requests", "commits", "workflow_runs",
            "build_artifacts", "deployments", "services", "environments",
        ]);
        Assert.Equal(1, counts["work_items"]);
        Assert.Equal(1, counts["pull_requests"]);
        Assert.Equal(2, counts["commits"]);          // be82d… + previous aa12e…
        Assert.Equal(2, counts["workflow_runs"]);    // 98122 + 98100
        Assert.Equal(2, counts["build_artifacts"]);
        Assert.Equal(2, counts["deployments"]);      // two real staging deployments
        Assert.Equal(1, counts["services"]);
        Assert.Equal(1, counts["environments"]);
    }

    [Fact]
    public async Task Reingesting_a_single_event_is_a_noop()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var connector = new FixtureConnector();
        var events = await CollectAllAsync(connector);

        var result = await app.IngestAsync([events[0]]);

        Assert.Equal(1, result.Received);
        Assert.Equal(0, result.Applied);
        Assert.Equal(1, result.Duplicated);
    }

    [Fact]
    public async Task Observation_log_does_not_grow_on_duplicate_batches()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();
        var observationsAfterFirst = (await QueryCountsAsync(app, ["observations"]))["observations"];

        await app.IngestScenarioAsync();

        var observationsAfterSecond = (await QueryCountsAsync(app, ["observations"]))["observations"];
        Assert.Equal(observationsAfterFirst, observationsAfterSecond);
    }

    internal static async Task<List<TracebackEvent>> CollectAllAsync(FixtureConnector connector)
    {
        var events = new List<TracebackEvent>();
        await foreach (var evt in connector.CollectAsync())
            events.Add(evt);
        return events;
    }

    internal static async Task<Dictionary<string, int>> QueryCountsAsync(TracebackApp app, IReadOnlyList<string> tables)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        var results = new Dictionary<string, int>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{table}\"";
            results[table] = Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return results;
    }
}
