using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        services.AddOptions<GitHubConnectorOptions>()
            .Bind(configuration.GetSection(GitHubConnectorOptions.SectionName))
            .Configure(options => ConfigureComposeRepository(options, configuration))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GitHubConnectorOptions>, GitHubConnectorOptionsValidator>();
        services.AddSingleton<IGitHubTokenProvider, ConfiguredGitHubTokenProvider>();

        services.AddHttpClient<GitHubRestClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<GitHubConnectorOptions>>().Value;
            client.BaseAddress = new Uri(options.ApiBaseUrl!, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(100);
        });

        services.AddSingleton<IGitHubApiClient>(sp => sp.GetRequiredService<GitHubRestClient>());
        services.AddSingleton<IRepositorySyncSource, GitHubRepositorySyncSource>();
        services.AddSingleton<GitHubRepositorySyncSource.IOptionsMonitorHolder, GitHubRepositorySyncSource.OptionsMonitorHolder>();

        return services;
    }

    private static void ConfigureComposeRepository(GitHubConnectorOptions options, IConfiguration configuration)
    {
        // docker-compose keeps these optional variables outside the indexed
        // GitHub section so an unset pair binds to an actual empty list.
        if (options.Repositories is { Count: > 0 })
            return;

        var owner = configuration["GITHUB_OWNER"]?.Trim();
        var name = configuration["GITHUB_REPO"]?.Trim();
        if (string.IsNullOrWhiteSpace(owner) && string.IsNullOrWhiteSpace(name))
            return;

        options.Repositories = [new GitHubRepositoryOptions { Owner = owner, Name = name }];
    }
}
