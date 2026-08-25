using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

/// <summary>
/// Verifies the completion-criterion path: start the app with fixture seeding
/// enabled (the same behavior docker-compose configures) and query the chain.
/// </summary>
[Collection(PostgresTestCollection.Name)]
public sealed class ApiStartupSeedingTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Startup_seeding_makes_BOS_2268_queryable_immediately()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: true);

        var chain = await app.Client.GetJsonAsync("/api/work-items/BOS-2268/deployment");

        Assert.Equal("BOS-2268", chain.GetProperty("workItem").GetProperty("key").GetString());
        var pr = chain.GetProperty("chains")[0].GetProperty("pullRequest");
        Assert.Equal(1842, pr.GetProperty("number").GetInt32());
    }
}
