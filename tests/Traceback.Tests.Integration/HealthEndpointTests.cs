using System.Net;
using Npgsql;

namespace Traceback.Tests.Integration;

[Collection(PostgresTestCollection.Name)]
public sealed class HealthEndpointTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task Liveness_and_readiness_are_healthy_when_postgres_is_available()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);

        var live = await app.Client.GetAsync("/healthz/live");
        var ready = await app.Client.GetAsync("/healthz/ready");
        var compatibility = await app.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, compatibility.StatusCode);
        Assert.Equal("{\"status\":\"healthy\"}", await live.Content.ReadAsStringAsync());
        Assert.Equal("{\"status\":\"healthy\"}", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_dependency_failure_without_connection_details()
    {
        await using var app = await TracebackApp.StartAsync(postgres.Container, seedFixturesOnStartup: false);

        await DropDatabaseAsync(postgres.Container.GetConnectionString(), app.DatabaseName);

        var ready = await app.Client.GetAsync("/healthz/ready");
        var live = await app.Client.GetAsync("/healthz/live");
        var body = await ready.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("{\"status\":\"unhealthy\"}", body);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.ConnectionString, body, StringComparison.Ordinal);
    }

    private static async Task DropDatabaseAsync(string connectionString, string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = "postgres",
        };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }
}
