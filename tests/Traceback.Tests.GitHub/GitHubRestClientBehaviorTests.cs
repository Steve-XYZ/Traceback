using System.Net;
using System.Text;
using Traceback.Connectors.GitHub;
using Traceback.Tests.GitHubSupport;

namespace Traceback.Tests.GitHub;

/// <summary>
/// Transport-level behavior of the REST client: bounded retries, deliberate
/// rate-limit handling, no blind retries on auth/not-found, and credential
/// redaction in failures.
/// </summary>
public sealed class GitHubRestClientBehaviorTests : IDisposable
{
    private readonly ScriptedHandler _handler = new();
    private readonly GitHubRestClient _client;

    public GitHubRestClientBehaviorTests()
    {
        var options = new GitHubConnectorOptions
        {
            Token = "tb-secret-token-value",
            MaxRetries = 3,
            RetryBackoffSeconds = 0,
            MaxRateLimitWaitSeconds = 1,
        };
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://api.github.test/") };
        _client = new GitHubRestClient(httpClient, new StaticTokenProvider(options.Token!), new TestOptionsMonitor<GitHubConnectorOptions>(options));
    }

    public void Dispose() => _handler.Dispose();

    [Fact]
    public async Task Transient_5xx_responses_are_retried_within_bounds_then_succeed()
    {
        _handler.Queue(Response(500), Response(503), Response(200));

        var repo = await _client.GetRepositoryAsync("acme", "player-manager");

        Assert.Equal("acme/player-manager", repo.FullName);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task Persistent_5xx_fails_with_transient_exception_after_bounded_attempts()
    {
        for (var i = 0; i < 10; i++)
            _handler.Queue(Response(500));

        await Assert.ThrowsAsync<GitHubTransientException>(() => _client.GetRepositoryAsync("acme", "player-manager"));

        // Initial attempt + MaxRetries retries; never an unbounded loop.
        Assert.Equal(4, _handler.Requests.Count);
    }

    [Fact]
    public async Task Authentication_failures_are_not_retried()
    {
        _handler.Queue(Response(401), Response(200));

        await Assert.ThrowsAsync<GitHubAuthenticationException>(() => _client.GetRepositoryAsync("acme", "player-manager"));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task NotFound_is_not_retried()
    {
        _handler.Queue(Response(404), Response(200));

        await Assert.ThrowsAsync<GitHubNotFoundException>(() => _client.GetRepositoryAsync("acme", "missing"));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task Primary_rate_limit_with_distant_reset_fails_fast_with_reset_information()
    {
        var resetEpoch = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        _handler.Queue(Response(403, headers: new Dictionary<string, string>
        {
            ["x-ratelimit-remaining"] = "0",
            ["x-ratelimit-reset"] = resetEpoch,
        }));

        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(
            () => _client.GetRepositoryAsync("acme", "player-manager"));

        Assert.NotNull(exception.ResetAt);
        Assert.Single(_handler.Requests); // No hot retry loop.
    }

    [Fact]
    public async Task Secondary_rate_limit_with_short_retry_after_waits_and_recovers()
    {
        _handler.Queue(Response(429, headers: new Dictionary<string, string> { ["Retry-After"] = "0" }), Response(200));

        var repo = await _client.GetRepositoryAsync("acme", "player-manager");

        Assert.Equal("acme/player-manager", repo.FullName);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    public async Task Repeated_short_rate_limits_fail_after_one_bounded_wait(int status)
    {
        var headers = new Dictionary<string, string> { ["Retry-After"] = "0" };
        if (status == 403)
        {
            headers["x-ratelimit-remaining"] = "0";
            headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        _handler.Queue(Response(status, headers), Response(status, headers), Response(200));

        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(
            () => _client.GetRepositoryAsync("acme", "player-manager"));

        Assert.Contains("single configured wait", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Requests_carry_the_authorization_header_and_user_agent()
    {
        _handler.Queue(Response(200));

        await _client.GetRepositoryAsync("acme", "player-manager");

        Assert.Equal("Bearer tb-secret-token-value", _handler.Requests[0].Headers.Authorization?.ToString());
        Assert.Contains(_handler.Requests[0].Headers.UserAgent, h => h.Product is { Name: "traceback" });
    }

    [Fact]
    public async Task GitHub_enterprise_api_path_is_preserved_for_relative_requests()
    {
        using var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(GitHubConnectorOptions.NormalizeApiBaseUrl("https://ghe.example/api/v3")),
        };
        var client = new GitHubRestClient(
            httpClient,
            new StaticTokenProvider("token"),
            new TestOptionsMonitor<GitHubConnectorOptions>(new GitHubConnectorOptions()));
        _handler.Queue(Response(200));

        await client.GetRepositoryAsync("acme", "player-manager");

        Assert.Equal("/api/v3/repos/acme/player-manager", _handler.Requests[0].RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Failures_never_expose_the_token()
    {
        for (var i = 0; i < 10; i++)
            _handler.Queue(Response(500));

        var exception = await Assert.ThrowsAsync<GitHubTransientException>(
            () => _client.GetRepositoryAsync("acme", "player-manager"));

        Assert.DoesNotContain("tb-secret-token-value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("tb-secret-token-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_header_next_page_parsing_matches_github_format()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Link",
            $"<https://api.github.test/repos/acme/player/pulls?state=all&page=2&per_page=2>; rel=\"next\", " +
            "<https://api.github.test/repos/acme/player/pulls?state=all&page=9>; rel=\"last\"");

        var next = GitHubRestClient.ParseNextLink(response.Headers);

        Assert.Equal("https://api.github.test/repos/acme/player/pulls?state=all&page=2&per_page=2", next);
    }

    private static HttpResponseMessage Response(int status, Dictionary<string, string>? headers = null)
    {
        var body = status == 200
            ? """{"id":1,"full_name":"acme/player-manager","owner":{"login":"acme"},"name":"player-manager"}"""
            : """{"message":"error"}""";
        var message = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
            foreach (var (key, value) in headers)
                message.Headers.TryAddWithoutValidation(key, value);
        return message;
    }
}
