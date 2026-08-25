using Microsoft.Extensions.Options;

namespace Traceback.Connectors.GitHub;

/// <summary>Resolves the API token from configuration or a token file.</summary>
public interface IGitHubTokenProvider
{
    /// <summary>The current token. Implementations never expose it beyond callers building request headers.</summary>
    string GetToken();
}

internal sealed class ConfiguredGitHubTokenProvider(IOptionsMonitor<GitHubConnectorOptions> options) : IGitHubTokenProvider
{
    private readonly object _lock = new();
    private string? _cached;
    private string? _cachedFromFile;
    private DateTimeOffset _fileCheckedAt;

    public string GetToken()
    {
        var options1 = options.CurrentValue;
        var inline = options1.Token?.Trim();
        if (!string.IsNullOrEmpty(inline))
            return inline;

        if (string.IsNullOrWhiteSpace(options1.TokenFile))
            throw new InvalidOperationException(
                "GitHub access is not configured. Provide a token via the GitHub:Token user secret/environment variable or GitHub:TokenFile.");

        lock (_lock)
        {
            // Re-read periodically so rotated secrets are picked up without restart.
            if (_cached is not null && DateTimeOffset.UtcNow - _fileCheckedAt < TimeSpan.FromSeconds(30))
                return _cached;
            var content = File.ReadAllText(options1.TokenFile).Trim();
            if (content.Length == 0)
                throw new InvalidOperationException($"The file configured at GitHub:TokenFile is empty.");
            _cachedFromFile = content;
            _fileCheckedAt = DateTimeOffset.UtcNow;
            _cached = _cachedFromFile;
            return _cached;
        }
    }
}
