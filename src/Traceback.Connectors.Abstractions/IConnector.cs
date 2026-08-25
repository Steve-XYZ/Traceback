namespace Traceback.Connectors.Abstractions;

/// <summary>
/// A connector translates one external system's data into normalized Traceback
/// events. It knows nothing about persistence, the domain model, or other
/// connectors. Live connectors will poll or receive webhooks and emit events
/// through this same contract.
/// </summary>
public interface IConnector
{
    /// <summary>Stable connector name, e.g. "linear", "github". Used as event provider.</summary>
    string Name { get; }

    /// <summary>Collects the current batch of observations from the source.</summary>
    IAsyncEnumerable<TracebackEvent> CollectAsync(CancellationToken cancellationToken = default);
}
