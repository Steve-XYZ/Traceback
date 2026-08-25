using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure;

/// <summary>
/// Deterministic design-time context for `dotnet ef` so migrations never depend
/// on the API host starting cleanly.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TracebackDbContext>
{
    public TracebackDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnectionString();
        var options = new DbContextOptionsBuilder<TracebackDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new TracebackDbContext(options);
    }

    public static string ResolveConnectionString()
    {
        const string envVar = "TRACEBACK_CONNECTIONSTRING";
        return Environment.GetEnvironmentVariable(envVar)
            ?? "Host=localhost;Port=54329;Database=traceback;Username=traceback;Password=traceback";
    }
}
