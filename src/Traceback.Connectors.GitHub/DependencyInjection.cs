using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Traceback.Connectors.Abstractions;

namespace Traceback.Connectors.GitHub;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the GitHub connector: options binding, token provider, HTTP
    /// client with auth/retry/rate-limit handling, the REST client, and the
    /// repository synchronization source. Read-only by construction — nothing
    /// in this assembly performs a GitHub write.
    /// </summary>
    public static IServiceCollection AddGitHubConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubConnectorOptions>(configuration.GetSection(GitHubConnectorOptions.SectionName));
        services.AddSingleton<IGitHubTokenProvider, ConfiguredGitHubTokenProvider>();

        var apiBaseUrl = configuration.GetSection(GitHubConnectorOptions.SectionName)["ApiBaseUrl"] ?? "https://api.github.com/";
        services.AddHttpClient<GitHubRestClient>(client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(100);
        });

        services.AddSingleton<IGitHubApiClient>(sp => sp.GetRequiredService<GitHubRestClient>());
        services.AddSingleton<IRepositorySyncSource, GitHubRepositorySyncSource>();
        services.AddSingleton<GitHubRepositorySyncSource.IOptionsMonitorHolder, GitHubRepositorySyncSource.OptionsMonitorHolder>();

        return services;
    }
}
