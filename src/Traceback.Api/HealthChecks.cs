using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Api;

/// <summary>Checks the database from a short-lived scope created per probe.</summary>
internal sealed partial class PostgresHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<PostgresHealthCheck> logger) : IHealthCheck
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL readiness check failed: {ExceptionType}")]
    private partial void LogFailure(string exceptionType);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TracebackDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Keep provider details (including any connection metadata) out of
            // both the health response and logs. The status code is the signal.
            LogFailure(exception.GetType().Name);
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}

/// <summary>Builds the two tagged health surfaces and a stable redacted response.</summary>
internal static class HealthCheckEndpoints
{
    public static HealthCheckOptions ForTag(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        ResponseWriter = WriteResponseAsync,
    };

    private static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var status = report.Status switch
        {
            HealthStatus.Healthy => "healthy",
            HealthStatus.Degraded => "degraded",
            _ => "unhealthy",
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { status }));
    }
}
