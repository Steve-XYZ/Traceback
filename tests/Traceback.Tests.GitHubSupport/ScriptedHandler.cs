using System.Text;
using System.Text.Json;

namespace Traceback.Tests.GitHubSupport;

/// <summary>HttpMessageHandler serving a scripted sequence of responses, for transport tests.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Queue(HttpResponseMessage response) => _responses.Enqueue(response);

    public void Queue(params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
            throw new InvalidOperationException("No scripted response available; the client issued an unexpected extra request.");
        return Task.FromResult(_responses.Dequeue());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            while (_responses.Count > 0)
                _responses.Dequeue().Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Serialization helper for scripted JSON bodies.</summary>
public static class JsonBody
{
    public static StringContent From(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
