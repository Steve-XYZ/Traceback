using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Traceback.Application.Ingestion;
using Traceback.Application.Queries;
using Traceback.Benchmark;
using Traceback.Connectors.GitHub;
using Traceback.Infrastructure;
using Traceback.Infrastructure.Persistence;
using Traceback.Tests.GitHubSupport;

// Development benchmark for the GitHub synchronization path. It runs the real
// pipeline - connector, normalized events, ingestion, PostgreSQL, query layer -
// against a generated GitHub-shaped repository served over the fake transport,
// so the numbers describe Traceback's own cost rather than network latency.
//
//   dotnet run -c Release --project tests/Traceback.Benchmark
//
// Requires a Docker daemon (PostgreSQL runs in a throwaway container).

// `--scale small` runs a tenth of the data: useful for checking whether a cost
// grows linearly with the corpus or worse.
var scale = args.Contains("--scale", StringComparer.Ordinal) &&
    Array.IndexOf(args, "--scale") + 1 < args.Length &&
    args[Array.IndexOf(args, "--scale") + 1] == "small"
    ? BenchmarkScale.Small
    : BenchmarkScale.Default;
var now = DateTimeOffset.UtcNow;

Console.WriteLine($"Generating {scale.PullRequests} pull requests, " +
    $"{scale.PullRequests * scale.CommitsPerPullRequest + scale.StandaloneCommits} commits, " +
    $"{scale.WorkflowRuns} workflow runs over {scale.LookbackDays} days...");

var world = GeneratedRepository.Build(scale, now);
var handler = new FakeGitHubApiHandler { Repository = world };

await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
await postgres.StartAsync();
var connectionString = postgres.GetConnectionString();

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["GitHub:Token"] = "benchmark-token-not-a-real-credential",
        ["GitHub:ApiBaseUrl"] = "https://api.github.test/",
        ["GitHub:PageSize"] = "100",
        ["GitHub:InitialLookbackDays"] = scale.LookbackDays.ToString(CultureInfo.InvariantCulture),
        ["GitHub:IncrementalOverlapDays"] = "7",
        ["GitHub:MaxPagesPerFetch"] = "500",
    })
    .Build();

var services = new ServiceCollection();
// TB_BENCH_SQL=1 logs every executed command so a run can be broken down by
// statement shape instead of guessed at.
var logSql = Environment.GetEnvironmentVariable("TB_BENCH_SQL") == "1";
services.AddLogging(b =>
{
    b.SetMinimumLevel(logSql ? LogLevel.Information : LogLevel.Warning);
    b.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", logSql ? LogLevel.Information : LogLevel.Warning);
    b.AddSimpleConsole();
});
services.AddInfrastructure(connectionString);
services.AddGitHubConnector(configuration);
// Replace only the transport: everything above it is the production path.
for (var i = services.Count - 1; i >= 0; i--)
{
    if (services[i].ServiceType == typeof(IGitHubApiClient))
        services.RemoveAt(i);
}
services.AddSingleton<IGitHubApiClient>(sp => new GitHubRestClient(
    new HttpClient(handler) { BaseAddress = new Uri("https://api.github.test/") },
    new StaticTokenProvider("benchmark-token-not-a-real-credential"),
    sp.GetRequiredService<IOptionsMonitor<GitHubConnectorOptions>>()));

await using var provider = services.BuildServiceProvider();

using (var scope = provider.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<TracebackDbContext>().Database.MigrateAsync();
}

var results = new List<(string Metric, string Value)>();

// --- Pass 1: initial synchronization -----------------------------------------
var requestsBefore = handler.RequestLog.Count;
var initial = await SyncAsync(provider, scale.LookbackDays);
var initialRequests = handler.RequestLog.Count - requestsBefore;
Report("initial sync", initial, initialRequests);

var rowsAfterInitial = await CountRowsAsync(connectionString);

// --- Pass 2: no provider changes ---------------------------------------------
requestsBefore = handler.RequestLog.Count;
var idle = await SyncAsync(provider, scale.LookbackDays);
var idleRequests = handler.RequestLog.Count - requestsBefore;
Report("no-change sync", idle, idleRequests);

var rowsAfterIdle = await CountRowsAsync(connectionString);

// --- Read queries -------------------------------------------------------------
var (prMedian, prP95) = await MeasureAsync(provider, 40, async (queries, i) =>
{
    var number = 1 + (i * 7 % scale.PullRequests);
    var result = await queries.GetPullRequestContextAsync(GeneratedRepository.Owner, GeneratedRepository.Name, number);
    if (result is null)
        throw new InvalidOperationException($"PR #{number} missing");
});

var shas = await SampleShasAsync(connectionString, 40);
var (commitMedian, commitP95) = await MeasureAsync(provider, shas.Count, async (queries, i) =>
{
    var result = await queries.GetCommitDeliveryContextAsync(GeneratedRepository.Owner, GeneratedRepository.Name, shas[i]);
    if (result is null)
        throw new InvalidOperationException($"commit {shas[i]} missing");
});

var (changesMedian, changesP95) = await MeasureAsync(provider, 20, async (queries, _) =>
{
    await queries.ListChangesAsync(GeneratedRepository.Owner, GeneratedRepository.Name,
        now.AddDays(-scale.LookbackDays), now.AddDays(1), 50, cursor: null);
});

Console.WriteLine();
Console.WriteLine("| metric | value |");
Console.WriteLine("|---|---|");
foreach (var (metric, value) in results)
    Console.WriteLine($"| {metric} | {value} |");
Console.WriteLine($"| pull request context (median / p95) | {prMedian:F1} ms / {prP95:F1} ms |");
Console.WriteLine($"| commit delivery context (median / p95) | {commitMedian:F1} ms / {commitP95:F1} ms |");
Console.WriteLine($"| repository changes, 50 entries (median / p95) | {changesMedian:F1} ms / {changesP95:F1} ms |");
Console.WriteLine();
Console.WriteLine("| table | after initial sync | after no-change sync |");
Console.WriteLine("|---|---|---|");
foreach (var table in rowsAfterInitial.Keys.OrderBy(k => k, StringComparer.Ordinal))
    Console.WriteLine($"| {table} | {rowsAfterInitial[table]} | {rowsAfterIdle[table]} |");

void Report(string label, RepositorySyncResult result, int requests)
{
    var duration = (result.CompletedAt - result.StartedAt).TotalSeconds;
    if (!result.Success)
        throw new InvalidOperationException($"{label} failed: {result.Error}");
    results.Add(($"{label} duration", $"{duration:F1} s"));
    results.Add(($"{label} GitHub API requests", requests.ToString(CultureInfo.InvariantCulture)));
    results.Add(($"{label} resources inspected", result.TotalInspected.ToString(CultureInfo.InvariantCulture)));
    results.Add(($"{label} observations received", result.TotalObservationsReceived.ToString(CultureInfo.InvariantCulture)));
    results.Add(($"{label} observations applied", result.TotalObservationsApplied.ToString(CultureInfo.InvariantCulture)));
    results.Add(($"{label} duplicates", result.TotalDuplicates.ToString(CultureInfo.InvariantCulture)));
    foreach (var resource in result.Resources)
        results.Add(($"{label} · {resource.ResourceType}", $"{resource.DurationMs / 1000:F1} s"));
}

static async Task<RepositorySyncResult> SyncAsync(IServiceProvider provider, int lookbackDays)
{
    using var scope = provider.CreateScope();
    var synchronizer = scope.ServiceProvider.GetRequiredService<IRepositorySynchronizer>();
    return await synchronizer.SynchronizeAsync("github",
        new RepositorySyncRequest($"{GeneratedRepository.Owner}/{GeneratedRepository.Name}", lookbackDays));
}

static async Task<(double Median, double P95)> MeasureAsync(
    IServiceProvider provider, int iterations, Func<ISourceControlQueries, int, Task> action)
{
    var samples = new List<double>(iterations);
    for (var i = 0; i < iterations; i++)
    {
        using var scope = provider.CreateScope();
        var queries = scope.ServiceProvider.GetRequiredService<ISourceControlQueries>();
        var stopwatch = Stopwatch.StartNew();
        await action(queries, i);
        stopwatch.Stop();
        samples.Add(stopwatch.Elapsed.TotalMilliseconds);
    }
    samples.Sort();
    return (samples[samples.Count / 2], samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))]);
}

static async Task<Dictionary<string, long>> CountRowsAsync(string connectionString)
{
    string[] tables =
    [
        "source_repositories", "pull_requests", "commits", "workflow_runs", "build_artifacts",
        "engineers", "pull_request_commits", "workflow_run_artifacts", "external_identities", "observations",
    ];
    var counts = new Dictionary<string, long>();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    foreach (var table in tables)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM \"{table}\"";
        counts[table] = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    return counts;
}

static async Task<List<string>> SampleShasAsync(string connectionString, int count)
{
    var shas = new List<string>(count);
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT sha FROM commits ORDER BY sha LIMIT " + count.ToString(CultureInfo.InvariantCulture);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
        shas.Add(reader.GetString(0));
    return shas;
}
