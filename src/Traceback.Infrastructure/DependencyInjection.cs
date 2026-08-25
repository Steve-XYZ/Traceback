using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Traceback.Application.Ingestion;
using Traceback.Application.Queries;
using Traceback.Infrastructure.Ingestion;
using Traceback.Infrastructure.Persistence;
using Traceback.Infrastructure.Queries;
using Traceback.Infrastructure.Sync;

namespace Traceback.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence, ingestion, synchronization, and query implementations. The only
    /// place where Npgsql specifics (jsonb via naming conventions, provider) live.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<TracebackDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IIngestionService, IngestionService>();
        services.AddScoped<IWorkItemQueries, WorkItemQueries>();
        services.AddScoped<IServiceQueries, ServiceQueries>();
        services.AddScoped<ISourceControlQueries, SourceControlQueries>();
        services.AddScoped<ISyncStateQueries, SyncStateQueries>();
        services.AddScoped<IRepositorySynchronizer, RepositorySynchronizer>();

        return services;
    }
}
