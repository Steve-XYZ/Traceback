using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.Fixtures;

namespace Traceback.Tests.Integration;

/// <summary>
/// One PostgreSQL container shared by the whole test collection; each
/// <see cref="TracebackApp"/> creates its own throwaway database and API host,
/// so test classes never observe each other's data.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix")]
public sealed class PostgresTestCollection : ICollectionFixture<PostgresContainerFixture>
{
    public const string Name = "postgres";
}

/// <summary>A disposable application instance backed by its own database.</summary>
public sealed class TracebackApp : IAsyncDisposable
{
    private WebApplicationFactory<Program>? _factory;
    private readonly bool _ownsDatabase;
    private readonly PostgreSqlContainer container;
    private readonly bool seedFixturesOnStartup;

    public TracebackApp(PostgreSqlContainer container, bool seedFixturesOnStartup, string? existingDatabaseName = null)
    {
        this.container = container;
        this.seedFixturesOnStartup = seedFixturesOnStartup;
        _ownsDatabase = existingDatabaseName is null;
        DatabaseName = existingDatabaseName ?? $"traceback_test_{Guid.NewGuid():N}";
    }

    public string DatabaseName { get; }

    public string ConnectionString { get; private set; } = null!;

    /// <summary>The underlying host factory; gives tests access to DI services.</summary>
    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("App not started.");

    public HttpClient Client => _factory?.CreateClient()
        ?? throw new InvalidOperationException("App not started.");

    public static async Task<TracebackApp> StartAsync(
        PostgreSqlContainer container,
        bool seedFixturesOnStartup,
        Action<IServiceCollection>? configureServices = null,
        IDictionary<string, string?>? settings = null)
    {
        var app = new TracebackApp(container, seedFixturesOnStartup);
        await app.InitializeAsync(configureServices, settings);
        return app;
    }

    /// <summary>
    /// Starts a new application instance against an existing database (used to
    /// prove that synchronization checkpoints survive a process restart). The
    /// returned instance does not drop the database on disposal.
    /// </summary>
    public static async Task<TracebackApp> RestartAgainstSameDatabaseAsync(
        PostgreSqlContainer container,
        string existingDatabaseName,
        Action<IServiceCollection>? configureServices = null,
        IDictionary<string, string?>? settings = null)
    {
        var app = new TracebackApp(container, seedFixturesOnStartup: false, existingDatabaseName);
        await app.InitializeCoreAsync(configureServices, settings, createDatabase: false);
        return app;
    }

    private async Task InitializeAsync(Action<IServiceCollection>? configureServices, IDictionary<string, string?>? settings) =>
        await InitializeCoreAsync(configureServices, settings, createDatabase: true);

    private async Task InitializeCoreAsync(Action<IServiceCollection>? configureServices, IDictionary<string, string?>? settings, bool createDatabase)
    {
        if (createDatabase)
        {
            var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Database = "postgres",
            };
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = DatabaseName,
        };
        ConnectionString = builder.ConnectionString;

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(web =>
            {
                web.UseEnvironment("Development");
                foreach (var setting in DefaultSettings())
                    web.UseSetting(setting.Key, setting.Value);
                if (settings is not null)
                {
                    foreach (var setting in settings)
                        web.UseSetting(setting.Key, setting.Value);
                }
                if (configureServices is not null)
                    web.ConfigureServices(configureServices);
            });
        // Forces host construction, which applies migrations (and optional seeding).
        _ = _factory.CreateClient();
    }

    private Dictionary<string, string> DefaultSettings() => new()
    {
        ["ConnectionStrings:Default"] = ConnectionString,
        ["MigrateOnStartup"] = "true",
        ["IngestFixturesOnStartup"] = seedFixturesOnStartup ? "true" : "false",
    };

    /// <summary>Runs the fixture scenario through the standard ingestion boundary.</summary>
    public async Task<IngestionResult> IngestScenarioAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
        var connector = new FixtureConnector();

        var events = new List<TracebackEvent>();
        await foreach (var evt in connector.CollectAsync())
            events.Add(evt);
        return await ingestion.IngestAsync(events);
    }

    /// <summary>Ingests an ad-hoc batch through the standard ingestion boundary.</summary>
    public async Task<IngestionResult> IngestAsync(IEnumerable<TracebackEvent> events)
    {
        using var scope = _factory!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIngestionService>().IngestAsync(events);
    }

    /// <summary>Resolves a scoped service from the application's DI container.</summary>
    public T GetRequiredService<T>() where T : notnull
    {
        using var scope = _factory!.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (!_ownsDatabase)
            return;

        try
        {
            var admin = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Database = "postgres",
            };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best-effort cleanup; the container dies with the test run anyway.
        }
    }
}
