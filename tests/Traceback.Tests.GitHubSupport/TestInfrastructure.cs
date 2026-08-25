using Microsoft.Extensions.Options;

namespace Traceback.Tests.GitHubSupport;

/// <summary>Fixed-value options monitor for tests.</summary>
public sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Token provider returning a fixed string; used instead of configuration in tests.</summary>
public sealed class StaticTokenProvider(string token) : Traceback.Connectors.GitHub.IGitHubTokenProvider
{
    public string GetToken() => token;
}
