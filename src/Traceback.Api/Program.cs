using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Traceback.Api;
using Traceback.Application.Ingestion;
using Traceback.Application.Queries;
using Traceback.Connectors.Abstractions;
using Traceback.Connectors.Fixtures;
using Traceback.Connectors.GitHub;
using Traceback.Infrastructure;
using Traceback.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.ConfigureObservability();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException("ConnectionStrings:Default must be configured outside Development.");

    connectionString = DesignTimeDbContextFactory.ResolveConnectionString();
}

builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live", "ready"])
    .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

// The fixture connector is a real connector: it enters through the same
// IConnector → IIngestionService boundary a live GitHub/Linear connector will use.
builder.Services.AddSingleton<IConnector, FixtureConnector>();

// Read-only GitHub synchronization (token via user secrets/environment, never files in source control).
builder.Services.AddGitHubConnector(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();

app.MapHealthChecks("/healthz/live", HealthCheckEndpoints.ForTag("live"));
app.MapHealthChecks("/healthz/ready", HealthCheckEndpoints.ForTag("ready"));
app.MapHealthChecks("/healthz", HealthCheckEndpoints.ForTag("ready"));

app.MapPost("/api/admin/ingest/fixtures", async (IConnector connector, IIngestionService ingestion, CancellationToken ct) =>
{
    var events = new List<TracebackEvent>();
    await foreach (var evt in connector.CollectAsync(ct))
        events.Add(evt);
    var result = await ingestion.IngestAsync(events, ct);
    return Results.Ok(result);
});

app.MapGet("/api/work-items/{key}/deployment", async (string key, IWorkItemQueries queries, CancellationToken ct) =>
{
    var result = await queries.GetDeploymentChainAsync(key, ct);
    return result is null ? Results.NotFound(new { message = $"Unknown work item '{key}'." }) : Results.Ok(result);
});

app.MapGet("/api/services/{service}/environments/{environment}/current-deployment",
    async (string service, string environment, IServiceQueries queries, CancellationToken ct) =>
{
    var result = await queries.GetCurrentDeploymentAsync(service, environment, ct);
    return result is null
        ? Results.NotFound(new { message = $"Unknown service '{service}' or environment '{environment}'." })
        : Results.Ok(result);
});

app.MapGet("/api/services/{service}/environments/{environment}/deployments",
    async (string service, string environment, DateTimeOffset? from, DateTimeOffset? to,
        IServiceQueries queries, CancellationToken ct) =>
{
    var toUtc = to ?? DateTimeOffset.UtcNow;
    var fromUtc = from ?? toUtc.AddHours(-24);
    var result = await queries.GetDeploymentHistoryAsync(service, environment, fromUtc, toUtc, ct);
    return result is null
        ? Results.NotFound(new { message = $"Unknown service '{service}' or environment '{environment}'." })
        : Results.Ok(result);
});

// --- Source-control read APIs (deterministic; repository-scoped) ---

app.MapGet("/api/repositories", async (ISourceControlQueries queries, CancellationToken ct) =>
{
    // Small convenience listing for discovery; full evidence lives on detail endpoints.
    var result = await queries.ListRepositoriesAsync(ct);
    return Results.Ok(result);
});

app.MapGet("/api/repositories/{owner}/{repo}/pull-requests/{number:int}",
    async (string owner, string repo, int number, ISourceControlQueries queries, CancellationToken ct) =>
{
    var result = await queries.GetPullRequestContextAsync(owner, repo, number, ct);
    return result is null
        ? Results.NotFound(new { message = $"Unknown pull request '{owner}/{repo}#{number}'." })
        : Results.Ok(result);
});

app.MapGet("/api/repositories/{owner}/{repo}/commits/{sha}/delivery-context",
    async (string owner, string repo, string sha, ISourceControlQueries queries, CancellationToken ct) =>
{
    if (!sha.All(char.IsAsciiHexDigit))
        return Results.BadRequest(new { message = "Commit SHA must be hexadecimal." });
    var result = await queries.GetCommitDeliveryContextAsync(owner, repo, sha, ct);
    return result is null
        ? Results.NotFound(new { message = $"Unknown commit '{sha}' in '{owner}/{repo}'." })
        : Results.Ok(result);
});

app.MapGet("/api/repositories/{owner}/{repo}/changes",
    async (string owner, string repo, DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor,
        ISourceControlQueries queries, CancellationToken ct) =>
{
    var toUtc = to ?? DateTimeOffset.UtcNow;
    var fromUtc = from ?? toUtc.AddDays(-7);
    var pageSize = Math.Clamp(limit ?? 50, 1, 200);
    if (cursor is not null && !ChangesCursorCodec.TryDecode(cursor, out _, out _, out _))
        return Results.BadRequest(new { message = "Invalid continuation cursor." });
    var result = await queries.ListChangesAsync(owner, repo, fromUtc, toUtc, pageSize, cursor, ct);
    return result is null
        ? Results.NotFound(new { message = $"Unknown repository '{owner}/{repo}'." })
        : Results.Ok(result);
});

// --- Administrative integration endpoints ---
//
// These trigger synchronization and expose checkpoint state only. They never
// echo configuration values or credentials. Application-level authorization is
// required before any deployment beyond a local development machine.

app.MapPost("/api/admin/integrations/github/sync/{owner}/{repo}",
    async (string owner, string repo, IRepositorySynchronizer synchronizer,
        Microsoft.Extensions.Options.IOptions<GitHubConnectorOptions> options, CancellationToken ct) =>
{
    var config = options.Value.FindRepository(owner, repo);
    if (config is null)
        return Results.NotFound(new { message = $"Repository '{owner}/{repo}' is not configured for GitHub synchronization. Add it under GitHub:Repositories." });

    var lookback = config.InitialLookbackDays ?? options.Value.InitialLookbackDays;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromMinutes(30));
    var result = await synchronizer.SynchronizeAsync("github", new RepositorySyncRequest(config.Key, lookback, timeout.Token), ct);
    return Results.Ok(result);
})
.Produces<RepositorySyncResult>();

app.MapGet("/api/admin/integrations/github/status", async (ISyncStateQueries states, CancellationToken ct) =>
    Results.Ok(await states.GetStatesAsync("github", ct)));

// Apply pending EF Core migrations on startup (Development default; disable or
// enable explicitly with MigrateOnStartup).
if (app.Configuration.GetValue<bool?>("MigrateOnStartup") ?? app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TracebackDbContext>();
    await db.Database.MigrateAsync();

    // Seed the fixture scenario through the normal ingestion boundary when enabled.
    // Ingestion is idempotent, so re-running on every start is harmless.
    if (app.Configuration.GetValue("IngestFixturesOnStartup", false))
    {
        var connector = scope.ServiceProvider.GetRequiredService<IConnector>();
        var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
        var events = new List<TracebackEvent>();
        await foreach (var evt in connector.CollectAsync())
            events.Add(evt);
        await ingestion.IngestAsync(events);
    }
}

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
