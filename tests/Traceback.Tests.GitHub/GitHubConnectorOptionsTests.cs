using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Traceback.Connectors.GitHub;

namespace Traceback.Tests.GitHub;

public sealed class GitHubConnectorOptionsTests
{
    private static readonly GitHubConnectorOptionsValidator Validator = new();

    [Fact]
    public void Empty_repositories_and_token_are_valid_for_fixture_only_runs()
    {
        var result = Validator.Validate(Options.DefaultName, new GitHubConnectorOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Empty_repository_list_is_valid_for_fixture_only_runs()
    {
        var result = Validate(new GitHubConnectorOptions
        {
            Repositories = [],
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Page_size_zero_is_rejected_before_the_fetch_divide_by_zero_path()
    {
        var result = Validate(new GitHubConnectorOptions { PageSize = 0 });

        Assert.Contains(Failures(result), failure => failure.Contains("PageSize", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Page_size_must_be_between_one_and_one_hundred(int pageSize)
    {
        var result = Validate(new GitHubConnectorOptions { PageSize = pageSize });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Other_limits_and_repository_lookback_must_be_nonnegative()
    {
        var result = Validate(new GitHubConnectorOptions
        {
            MaxPagesPerFetch = 0,
            InitialLookbackDays = -1,
            IncrementalOverlapDays = -1,
            MaxRetries = -1,
            RetryBackoffSeconds = -1,
            MaxRateLimitWaitSeconds = -1,
            Repositories = [new GitHubRepositoryOptions { Owner = "acme", Name = "repo", InitialLookbackDays = -1 }],
        });

        Assert.False(result.Succeeded);
        Assert.Equal(7, Failures(result).Count);
    }

    [Fact]
    public void Api_base_url_must_be_an_absolute_http_url()
    {
        var relative = Validate(new GitHubConnectorOptions { ApiBaseUrl = "/api" });
        var ftp = Validate(new GitHubConnectorOptions { ApiBaseUrl = "ftp://github.test/" });

        Assert.False(relative.Succeeded);
        Assert.False(ftp.Succeeded);
    }

    [Theory]
    [InlineData("https://ghe.example/api/v3?tenant=acme")]
    [InlineData("https://ghe.example/api/v3#api")]
    public void Api_base_url_must_not_include_a_query_or_fragment(string configured)
    {
        var result = Validate(new GitHubConnectorOptions { ApiBaseUrl = configured });

        Assert.Contains(Failures(result), failure => failure.Contains("query string or fragment", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://api.github.com", "https://api.github.com/")]
    [InlineData(" https://ghe.example/api/v3 ", "https://ghe.example/api/v3/")]
    [InlineData("https://ghe.example/api/v3///", "https://ghe.example/api/v3/")]
    public void Api_base_url_is_normalized_as_a_directory(string configured, string expected)
    {
        Assert.Equal(expected, GitHubConnectorOptions.NormalizeApiBaseUrl(configured));
    }

    [Fact]
    public void Registered_options_normalize_a_github_enterprise_api_path()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ApiBaseUrl"] = "https://ghe.example/api/v3",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGitHubConnector(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GitHubConnectorOptions>>().Value;

        Assert.Equal("https://ghe.example/api/v3/", options.ApiBaseUrl);
    }

    [Fact]
    public void Null_and_blank_repository_binding_is_reported_without_throwing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:ApiBaseUrl"] = " ",
                ["GitHub:Repositories:0:Owner"] = null,
                ["GitHub:Repositories:0:Name"] = "repo",
                ["GitHub:Repositories:1:Owner"] = "acme",
                ["GitHub:Repositories:1:Name"] = "  ",
            })
            .Build();
        var options = new GitHubConnectorOptions();
        configuration.GetSection(GitHubConnectorOptions.SectionName).Bind(options);

        var result = Validate(options);

        Assert.False(result.Succeeded);
        Assert.Contains(Failures(result), failure => failure.Contains("ApiBaseUrl", StringComparison.Ordinal));
        Assert.Contains(Failures(result), failure => failure.Contains("Owner", StringComparison.Ordinal));
        Assert.Contains(Failures(result), failure => failure.Contains("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void Registered_options_fail_when_invalid_configuration_is_resolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:PageSize"] = "0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGitHubConnector(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GitHubConnectorOptions>>().Value);

        Assert.Contains("PageSize", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_repository_variables_bind_when_both_are_present()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GITHUB_OWNER"] = " acme ",
                ["GITHUB_REPO"] = " player-manager ",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGitHubConnector(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GitHubConnectorOptions>>().Value;

        var repository = Assert.Single(options.Repositories);
        Assert.Equal("acme", repository.Owner);
        Assert.Equal("player-manager", repository.Name);
    }

    [Fact]
    public void Partial_compose_repository_variables_are_rejected()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GITHUB_OWNER"] = "acme",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddGitHubConnector(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GitHubConnectorOptions>>().Value);

        Assert.Contains("Name", exception.Message, StringComparison.Ordinal);
    }

    private static ValidateOptionsResult Validate(GitHubConnectorOptions options) =>
        Validator.Validate(Options.DefaultName, options);

    private static IReadOnlyList<string> Failures(ValidateOptionsResult result) =>
        result.Failures?.ToList() ?? [];
}
