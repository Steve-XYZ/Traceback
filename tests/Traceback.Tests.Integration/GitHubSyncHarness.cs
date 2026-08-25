using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.GitHub;
using Traceback.Infrastructure.Persistence;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.Integration;

/// <summary>
/// Shared wiring for end-to-end GitHub synchronization tests: the full
/// pipeline executes against PostgreSQL with the connector's real REST client,
/// whose transport is replaced by a <see cref="FakeGitHubApiHandler"/>.
/// </summary>
internal static class GitHubSyncHarness
{
    public const string Owner = "acme";
    public const string Name = "player-manager";
    public const string TokenSentinel = "tb-test-token-do-not-leak";

    /// <summary>A fresh fake repository with deterministic identity.</summary>
    public static FakeGitHubRepository NewWorld() => new() { Owner = Owner, Name = Name };

    public static IDictionary<string, string?> DefaultSettings(string pageSize = "100") => new Dictionary<string, string?>
    {
        ["MigrateOnStartup"] = "true",
        ["IngestFixturesOnStartup"] = "false",
        ["GitHub:Token"] = TokenSentinel,
        ["GitHub:PageSize"] = pageSize,
        ["GitHub:InitialLookbackDays"] = "30",
        ["GitHub:IncrementalOverlapDays"] = "7",
        ["GitHub:RetryBackoffSeconds"] = "0",
        ["GitHub:MaxRateLimitWaitSeconds"] = "1",
        ["GitHub:Repositories:0:Owner"] = Owner,
        ["GitHub:Repositories:0:Name"] = Name,
        ["GitHub:Repositories:0:InitialLookbackDays"] = "30",
    };

    public static Action<IServiceCollection> WireFakeTransport(params FakeGitHubRepository[] worlds)
    {
        var primary = worlds[0];
        var handler = new FakeGitHubApiHandler { Repository = primary };
        foreach (var extra in worlds.Skip(1))
            handler.ExtraRepositories.Add(extra);
        return WireFakeTransport(handler);
    }

    public static Action<IServiceCollection> WireFakeTransport(FakeGitHubApiHandler handler) => services =>
    {
        services.RemoveAll<IGitHubApiClient>();
        services.AddSingleton<IGitHubApiClient>(sp => new GitHubRestClient(
            new HttpClient(handler) { BaseAddress = new Uri($"https://{Owner}.github.test/") },
            new StaticTokenProvider(TokenSentinel),
            sp.GetRequiredService<IOptionsMonitor<GitHubConnectorOptions>>()));
    };

    /// <summary>Synchronizes one repository through the real orchestrator.</summary>
    public static async Task<RepositorySyncResult> SyncAsync(TracebackApp app, string? repositoryKey = null, int initialLookbackDays = 30)
    {
        using var scope = app.Factory.Services.CreateScope();
        var synchronizer = scope.ServiceProvider.GetRequiredService<IRepositorySynchronizer>();
        return await synchronizer.SynchronizeAsync(
            "github",
            new RepositorySyncRequest(repositoryKey ?? $"{Owner}/{Name}", initialLookbackDays));
    }

    public static async Task<int> CountRowsAsync(TracebackApp app, string table)
    {
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs an ad-hoc SQL query and returns the first column of each row as text.</summary>
    public static async Task<List<string>> QueryAsync(TracebackApp app, string sql, params object[] parameters)
    {
        var results = new List<string>();
        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
            command.Parameters.AddWithValue(parameters[i]);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(reader.IsDBNull(0) ? "" : reader.GetValue(0).ToString()!);
        return results;
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services) where T : class
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(T))
                services.RemoveAt(i);
        }
    }
}
