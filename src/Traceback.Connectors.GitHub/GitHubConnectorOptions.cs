namespace Traceback.Connectors.GitHub;

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
        Repositories.FirstOrDefault(r =>
            r.Owner.Equals(owner.Trim(), StringComparison.OrdinalIgnoreCase) &&
            r.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed class GitHubRepositoryOptions
{
    public required string Owner { get; set; }
    public required string Name { get; set; }

    /// <summary>History depth of the first synchronization; defaults to the connector-level setting.</summary>
    public int? InitialLookbackDays { get; set; }

    public string Key => $"{Owner}/{Name}".ToLowerInvariant();
}
