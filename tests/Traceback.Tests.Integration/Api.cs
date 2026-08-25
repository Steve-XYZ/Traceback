using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Traceback.Tests.Integration;

/// <summary>Shared helpers for asserting API responses.</summary>
public static class Api
{
    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string requestUri)
    {
        var response = await client.GetAsync(requestUri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return payload;
    }

    public static async Task<HttpStatusCode> GetStatusAsync(this HttpClient client, string requestUri)
    {
        var response = await client.GetAsync(requestUri);
        return response.StatusCode;
    }

    /// <summary>All sources across a node list must be non-empty.</summary>
    public static void AssertHasSources(JsonElement node, string context)
    {
        Assert.True(node.TryGetProperty("sources", out var sources), $"{context} must expose sources");
        Assert.True(sources.GetArrayLength() > 0, $"{context} must have at least one source");
        foreach (var source in sources.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("provider").GetString()), $"{context} source provider missing");
            Assert.False(string.IsNullOrWhiteSpace(source.GetProperty("externalKey").GetString()), $"{context} source external key missing");
            Assert.True(source.TryGetProperty("lastObservedAt", out _), $"{context} source must carry observation timestamps");
        }
    }
}
