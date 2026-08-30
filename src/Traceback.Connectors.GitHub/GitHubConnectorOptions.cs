namespace Traceback.Connectors.GitHub;

using Microsoft.Extensions.Options;

public sealed class GitHubConnectorOptions
{
    public const string SectionName = "GitHub";

    /// <summary>
    /// Token value. Prefer environment/user-secret injection (GitHub__Token);
    /// never commit a token to configuration files.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>Alternative: path to a file whose contents are the token (Docker secret style).</summary>
    public string? TokenFile { get; set; }

    /// <summary>API base URL; overridable for GitHub Enterprise Server and tests.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>
    /// Normalizes a valid API base URL as a directory so relative REST paths
    /// preserve a GitHub Enterprise Server path such as <c>/api/v3</c>.
    /// Invalid values are returned trimmed for the validator to report.
    /// </summary>
    internal static string NormalizeApiBaseUrl(string? apiBaseUrl)
    {
        var trimmed = apiBaseUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        var builder = new UriBuilder(uri);
        builder.Path = builder.Path.TrimEnd('/') + "/";
        return builder.Uri.AbsoluteUri;
    }

    public int InitialLookbackDays { get; set; } = 30;

    /// <summary>Incremental overlap window: streams re-inspect this many days behind their watermark so late-appearing updates (and Actions reruns) cannot be missed.</summary>
    public int IncrementalOverlapDays { get; set; } = 7;

    public int PageSize { get; set; } = 100;

    /// <summary>Safety cap on pages walked per stream pass; when hit, that stream fails before ingestion and its cursor is not advanced.</summary>
    public int MaxPagesPerFetch { get; set; } = 200;

    public int MaxRetries { get; set; } = 3;

    /// <summary>Base for exponential retry backoff, seconds. Zero in tests.</summary>
    public double RetryBackoffSeconds { get; set; } = 1.0;

    /// <summary>Wait at most this long inside the request pipeline when a rate limit allows a near-term reset; otherwise fail fast with the reset time.</summary>
    public int MaxRateLimitWaitSeconds { get; set; } = 120;

    public List<GitHubRepositoryOptions> Repositories { get; set; } = [];

    public GitHubRepositoryOptions? FindRepository(string owner, string name) =>
        Repositories?.FirstOrDefault(r =>
            r is not null &&
            string.Equals(r.Owner?.Trim(), owner.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class GitHubRepositoryOptions
{
    public string? Owner { get; set; }
    public string? Name { get; set; }

    /// <summary>History depth of the first synchronization; defaults to the connector-level setting.</summary>
    public int? InitialLookbackDays { get; set; }

    public string Key => $"{Owner?.Trim()}/{Name?.Trim()}".ToLowerInvariant();
}

/// <summary>Validates GitHub connector settings before the host starts.</summary>
public sealed class GitHubConnectorOptionsValidator : IValidateOptions<GitHubConnectorOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.PageSize is < 1 or > 100)
            failures.Add("GitHub:PageSize must be between 1 and 100.");
        if (options.MaxPagesPerFetch <= 0)
            failures.Add("GitHub:MaxPagesPerFetch must be greater than zero.");
        if (options.InitialLookbackDays < 0)
            failures.Add("GitHub:InitialLookbackDays must be nonnegative.");
        if (options.IncrementalOverlapDays < 0)
            failures.Add("GitHub:IncrementalOverlapDays must be nonnegative.");
        if (options.MaxRetries < 0)
            failures.Add("GitHub:MaxRetries must be nonnegative.");
        if (options.RetryBackoffSeconds < 0 || !double.IsFinite(options.RetryBackoffSeconds))
            failures.Add("GitHub:RetryBackoffSeconds must be a finite nonnegative number.");
        if (options.MaxRateLimitWaitSeconds < 0)
            failures.Add("GitHub:MaxRateLimitWaitSeconds must be nonnegative.");

        if (!Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiBaseUrl) ||
            (apiBaseUrl.Scheme != Uri.UriSchemeHttp && apiBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("GitHub:ApiBaseUrl must be an absolute HTTP or HTTPS URL.");
        }
        else if (!string.IsNullOrEmpty(apiBaseUrl.Query) || !string.IsNullOrEmpty(apiBaseUrl.Fragment))
        {
            failures.Add("GitHub:ApiBaseUrl must not include a query string or fragment.");
        }

        foreach (var (repository, index) in (options.Repositories ?? []).Select((repository, index) => (repository, index)))
        {
            if (repository is null)
            {
                failures.Add($"GitHub:Repositories:{index} must not be null.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(repository.Owner))
                failures.Add($"GitHub:Repositories:{index}:Owner must not be blank.");
            if (string.IsNullOrWhiteSpace(repository.Name))
                failures.Add($"GitHub:Repositories:{index}:Name must not be blank.");
            if (repository.InitialLookbackDays < 0)
                failures.Add($"GitHub:Repositories:{index}:InitialLookbackDays must be nonnegative.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
