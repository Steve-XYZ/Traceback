using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Traceback.Connectors.GitHub;

/// <summary>
/// REST client for the GitHub API. Behavior:
/// - Authorization header built per request from IGitHubTokenProvider; tokens
///   never appear in URLs, logs, exception messages, or telemetry.
/// - Bounded retries (default 3) with exponential backoff + jitter for network
///   errors, timeouts, and 408/500/502/503/504.
/// - Authentication and not-found failures fail immediately.
/// - Rate limits are detected via status plus x-ratelimit-remaining; when the
///   reset/retry-after is near, the client waits once inside a bounded window,
///   otherwise it fails fast with GitHubRateLimitException carrying the reset
///   time so synchronization can stop deliberately instead of hot-looping.
/// </summary>
internal sealed partial class GitHubRestClient(
    HttpClient httpClient,
    IGitHubTokenProvider tokenProvider,
    IOptionsMonitor<GitHubConnectorOptions> options) : IGitHubApiClient
{
    internal static readonly Meter Meter = new("Traceback.Sync");
    private static readonly Counter<long> ApiRequests =
        Meter.CreateCounter<long>("traceback.sync.api_requests", description: "GitHub API requests sent.");
    private static readonly Counter<long> ApiRetries =
        Meter.CreateCounter<long>("traceback.sync.api_retries", description: "Transient failures retried against the GitHub API.");
    private static readonly Counter<long> RateLimitEvents =
        Meter.CreateCounter<long>("traceback.sync.rate_limit_events", description: "GitHub rate-limit responses encountered.");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GitHubApiRepository> GetRepositoryAsync(string owner, string name, CancellationToken cancellationToken = default)
    {
        var repo = await GetObjectAsync<GitHubApiRepository>(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}",
            notFoundAsEmpty: false,
            cancellationToken);
        return repo ?? throw new GitHubNotFoundException($"GitHub repository '{owner}/{name}' does not exist.");
    }

    public async Task<GitHubArrayPage<GitHubApiPullRequest>> GetPullRequestsPageAsync(
        string owner, string name, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default)
    {
        var path = nextPageUrl ?? $"repos/{owner}/{name}/pulls?state=all&sort=updated&direction=desc&per_page={pageSize}";
        return await GetArrayPageAsync<GitHubApiPullRequest>(path, cancellationToken);
    }

    public async Task<GitHubArrayPage<GitHubApiCommit>> GetPullRequestCommitsPageAsync(
        string owner, string name, int number, string? nextPageUrl, int pageSize, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default)
    {
        var path = nextPageUrl ?? $"repos/{owner}/{name}/pulls/{number}/commits?per_page={pageSize}";
        return await GetArrayPageAsync<GitHubApiCommit>(path, cancellationToken, notFoundAsEmpty);
    }

    public async Task<GitHubArrayPage<GitHubApiCommit>> GetCommitsPageAsync(
        string owner, string name, DateTimeOffset? since, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default)
    {
        var path = nextPageUrl ?? $"repos/{owner}/{name}/commits?per_page={pageSize}" + FormatSince(since);
        return await GetArrayPageAsync<GitHubApiCommit>(path, cancellationToken);
    }

    public async Task<GitHubArrayPage<GitHubApiWorkflowRun>> GetWorkflowRunsPageAsync(
        string owner, string name, DateTimeOffset? createdFrom, string? nextPageUrl, int pageSize, CancellationToken cancellationToken = default)
    {
        var path = nextPageUrl ?? $"repos/{owner}/{name}/actions/runs?per_page={pageSize}" + FormatCreated(createdFrom);
        var page = await GetObjectPageAsync<GitHubApiWorkflowRunsPage>(path, cancellationToken);
        return new GitHubArrayPage<GitHubApiWorkflowRun>(page.Payload.WorkflowRuns ?? [], page.NextUrl);
    }

    public async Task<IReadOnlyList<GitHubApiWorkflowRun>> GetRunAttemptsAsync(
        string owner, string name, long runId, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default)
    {
        var page = await GetArrayPageAsync<GitHubApiWorkflowRun>(
            $"repos/{owner}/{name}/actions/runs/{runId}/attempts?per_page=100",
            cancellationToken, notFoundAsEmpty);
        // Attempts arrive oldest-first; keep that order for deterministic emission.
        return page.Items.OrderBy(a => a.RunAttempt).ToList();
    }

    public async Task<IReadOnlyList<GitHubApiArtifact>> GetRunArtifactsAsync(
        string owner, string name, long runId, bool notFoundAsEmpty = false, CancellationToken cancellationToken = default)
    {
        var artifacts = new List<GitHubApiArtifact>();
        var url = $"repos/{owner}/{name}/actions/runs/{runId}/artifacts?per_page={options.CurrentValue.PageSize}";
        while (url is not null)
        {
            var page = await GetObjectPageAsync<GitHubApiArtifactsPage>(url, cancellationToken, notFoundAsEmpty);
            if (page.Payload.Artifacts is { Count: > 0 })
                artifacts.AddRange(page.Payload.Artifacts);
            url = page.NextUrl;
        }
        return artifacts;
    }

    private static string FormatSince(DateTimeOffset? since) =>
        since is null ? "" : "&since=" + Uri.EscapeDataString(ToGitHubTimestamp(since.Value));

    private static string FormatCreated(DateTimeOffset? createdFrom) =>
        createdFrom is null ? "" : "&created=" + Uri.EscapeDataString(">=" + ToGitHubTimestamp(createdFrom.Value));

    private static string ToGitHubTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One page of a JSON-array endpoint.</summary>
    private async Task<GitHubArrayPage<T>> GetArrayPageAsync<T>(string pathOrUrl, CancellationToken ct, bool notFoundAsEmpty = false)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, pathOrUrl, notFoundAsEmpty, ct)
            ?? throw new InvalidOperationException("Unreachable: empty response without notFoundAsEmpty.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var items = await JsonSerializer.DeserializeAsync<List<T>>(stream, JsonOptions, ct) ?? [];
        return new GitHubArrayPage<T>(items, ParseNextLink(response.Headers));
    }

    /// <summary>One page of an endpoint returning a wrapper object with an array inside.</summary>
    private async Task<GitHubObjectPage<T>> GetObjectPageAsync<T>(string pathOrUrl, CancellationToken ct, bool notFoundAsEmpty = false)
    {
        using var response = await SendCoreAsync(HttpMethod.Get, pathOrUrl, notFoundAsEmpty, ct)
            ?? throw new InvalidOperationException("Unreachable: empty response without notFoundAsEmpty.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct)
            ?? throw new GitHubApiException("GitHub returned an empty payload where one was required.");
        return new GitHubObjectPage<T>(payload, ParseNextLink(response.Headers));
    }

    private async Task<T?> GetObjectAsync<T>(string path, bool notFoundAsEmpty, CancellationToken ct) where T : class
    {
        using var response = await SendCoreAsync(HttpMethod.Get, path, notFoundAsEmpty, ct);
        if (response is null)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    /// <summary>Returns null only when a 404 matched notFoundAsEmpty.</summary>
    private async Task<HttpResponseMessage?> SendCoreAsync(HttpMethod method, string pathOrUrl, bool notFoundAsEmpty, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        var attempts = 0;
        while (true)
        {
            using var request = BuildRequest(method, pathOrUrl);
            HttpResponseMessage response;
            ApiRequests.Add(1, new KeyValuePair<string, object?>("host", httpClient.BaseAddress?.Host ?? "api.github.com"));
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                attempts++;
                if (attempts > opts.MaxRetries)
                    throw new GitHubTransientException($"GitHub request failed after {opts.MaxRetries} retries: {ex.GetType().Name}");
                ApiRetries.Add(1, new KeyValuePair<string, object?>("reason", "network"));
                await DelayBackoffAsync(attempts, opts.RetryBackoffSeconds, ct);
                continue;
            }

            Activity.Current?.SetTag("traceback.github.path", RedactPath(pathOrUrl));
            Activity.Current?.SetTag("traceback.github.status", (int)response.StatusCode);

            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                if ((int)response.StatusCode == 404 && notFoundAsEmpty)
                    return null;

                // Rate limiting: primary (403 + remaining 0) or secondary (429 / Retry-After).
                var retryAfter = ParseRetryAfter(response);
                if ((int)response.StatusCode == 429 || IsPrimaryRateLimit(response))
                {
                    RateLimitEvents.Add(1, new KeyValuePair<string, object?>("host", httpClient.BaseAddress?.Host ?? "api.github.com"));
                    var resetAt = ParseRateLimitReset(response);
                    var waitSeconds = retryAfter ?? Math.Max(0, (int)Math.Ceiling(((resetAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalSeconds));
                    if (waitSeconds <= opts.MaxRateLimitWaitSeconds)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 1), ct);
                        continue;
                    }
                    throw new GitHubRateLimitException(
                        $"GitHub rate limit reached; resets at {(resetAt ?? DateTimeOffset.UtcNow):O} (retry-after {waitSeconds}s exceeds the configured {opts.MaxRateLimitWaitSeconds}s wait window).",
                        resetAt, retryAfter);
                }

                if ((int)response.StatusCode is 408 or 500 or 502 or 503 or 504)
                {
                    attempts++;
                    if (attempts > opts.MaxRetries)
                        throw new GitHubTransientException($"GitHub responded {(int)response.StatusCode} after {opts.MaxRetries} retries.");
                    ApiRetries.Add(1, new KeyValuePair<string, object?>("reason", "http"));
                    await DelayBackoffAsync(attempts, opts.RetryBackoffSeconds, ct);
                    continue;
                }

                if ((int)response.StatusCode == 404)
                    throw new GitHubNotFoundException($"GitHub resource does not exist ({RedactPath(pathOrUrl)}).");

                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    throw new GitHubAuthenticationException($"GitHub rejected the request ({(int)response.StatusCode}); check token validity and permissions.");

                throw new GitHubApiException($"GitHub request failed with HTTP {(int)response.StatusCode}.");
            }
        }
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string pathOrUrl)
    {
        var uri = pathOrUrl.StartsWith("http", StringComparison.Ordinal)
            ? new Uri(pathOrUrl)
            : new Uri(httpClient.BaseAddress!, pathOrUrl.StartsWith('/') ? pathOrUrl[1..] : pathOrUrl);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenProvider.GetToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("traceback");
        return request;
    }

    private static async Task DelayBackoffAsync(int attempt, double baseSeconds, CancellationToken ct)
    {
        var jitter = Random.Shared.NextDouble() * 0.5;
        var seconds = Math.Min(30, baseSeconds * Math.Pow(2, attempt - 1) * (1 + jitter));
        if (seconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }

    private static bool IsPrimaryRateLimit(HttpResponseMessage response) =>
        (int)response.StatusCode == 403 &&
        response.Headers.TryGetValues("x-ratelimit-remaining", out var values) &&
        values.FirstOrDefault() == "0";

    private static int? ParseRetryAfter(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Retry-After", out var values) &&
        int.TryParse(values.FirstOrDefault(), out var seconds)
            ? seconds
            : null;

    private static DateTimeOffset? ParseRateLimitReset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-reset", out var values) ||
            !long.TryParse(values.FirstOrDefault(), out var epoch))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    internal static string? ParseNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
            return null;
        foreach (var link in values)
        {
            // Example: <https://api.github.com/x?page=2>; rel="next", <...>; rel="last"
            foreach (var segment in link.Split(','))
            {
                var parts = segment.Split(';');
                if (parts.Length < 2)
                    continue;
                if (parts[1].Contains("rel=\"next\"", StringComparison.Ordinal))
                {
                    var url = parts[0].Trim().TrimStart('<').TrimEnd('>');
                    return url.Length == 0 ? null : url;
                }
            }
        }
        return null;
    }

    /// <summary>Paths carry no credentials; keep them short and stable in telemetry.</summary>
    private static string RedactPath(string pathOrUrl)
    {
        if (!pathOrUrl.StartsWith("http", StringComparison.Ordinal))
            return pathOrUrl;
        try
        {
            var uri = new Uri(pathOrUrl);
            return uri.PathAndQuery;
        }
        catch (UriFormatException)
        {
            return "[unparsable-url]";
        }
    }
}
