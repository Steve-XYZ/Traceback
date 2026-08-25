using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.Fixtures;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class UpdatedObservationTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Later_observation_updates_fields_without_creating_new_rows()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        var observedAt = DateTimeOffset.UtcNow;
        var update = new WorkItemObserved(
            new EventProvenance("linear", "work_item", FixtureConnector.WorkItemKey, null, observedAt, observedAt),
            FixtureConnector.WorkItemKey,
            "Roster page slow for very large seasons",
            Description: null,
            Status: "Closed",
            Type: null,
            Url: null,
            Assignee: null,
            ImplementsByPullRequests: []);

        var result = await app.IngestAsync([update]);
        Assert.Equal(1, result.Applied);

        var chain = await app.Client.GetJsonAsync($"/api/work-items/{FixtureConnector.WorkItemKey}/deployment");
        var workItem = chain.GetProperty("workItem");
        Assert.Equal("Roster page slow for very large seasons", workItem.GetProperty("title").GetString());
        Assert.Equal("Closed", workItem.GetProperty("status").GetString());

        var counts = await DuplicateIngestionTests.QueryCountsAsync(app,
            ["work_items", "pull_requests", "deployments"]);
        Assert.Equal(1, counts["work_items"]);
        Assert.Equal(1, counts["pull_requests"]);
        Assert.Equal(2, counts["deployments"]);
    }

    [Fact]
    public async Task Nulls_in_later_observations_preserve_known_values()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        await app.IngestScenarioAsync();

        // CommitObserved without message must not erase the known message.
        var observedAt = DateTimeOffset.UtcNow;
        var sparseCommit = new CommitObserved(
            new EventProvenance("github", "commit", FixtureConnector.CommitSha, null, observedAt, observedAt.AddSeconds(5)),
            FixtureConnector.CommitSha,
            Repository: null,
            Message: null,
            AuthoredAt: null,
            Author: new EngineerRef("Mira C.", null));

        await app.IngestAsync([sparseCommit]);

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT message FROM commits WHERE sha = '{FixtureConnector.CommitSha}'";
        var message = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(message);
        Assert.Contains("#1842", message);
    }

    [Fact]
    public async Task First_and_last_observation_timestamps_track_arrival()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);
        var firstBatch = await DuplicateIngestionTests.CollectAllAsync(new FixtureConnector());
        await app.IngestAsync(firstBatch);

        var later = DateTimeOffset.UtcNow.AddMinutes(10);
        var lateUpdate = new WorkItemObserved(
            new EventProvenance("linear", "work_item", FixtureConnector.WorkItemKey, null, later, later),
            FixtureConnector.WorkItemKey,
            Title: "Updated title",
            Description: null,
            Status: null,
            Type: null,
            Url: null,
            Assignee: null,
            ImplementsByPullRequests: []);
        await app.IngestAsync([lateUpdate]);

        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TracebackDbContext>();
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Key == FixtureConnector.WorkItemKey);

        Assert.Equal(firstBatch[^1].Provenance.ObservedAt, workItem.FirstObservedAt);
        Assert.True(workItem.LastObservedAt > workItem.FirstObservedAt);
        Assert.False(workItem.IsPlaceholder);
    }
}
