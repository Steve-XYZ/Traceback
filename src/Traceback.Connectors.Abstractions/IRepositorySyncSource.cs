namespace Traceback.Connectors.Abstractions;

/// <summary>
/// A connector-side port for incremental, cursor-based synchronization of one
/// repository. Unlike <see cref="IConnector"/> (whole-batch collection), an
/// implementation fetches per resource stream and reports the resume watermark
/// that is safe to persist once that stream's events have been ingested.
/// Implementations stay persistence-agnostic: cursors are opaque strings.
/// </summary>
public interface IRepositorySyncSource
{
    /// <summary>Stable provider name, e.g. "github".</summary>
    string Provider { get; }

    /// <summary>Resource streams in the order they should be synchronized.</summary>
    IReadOnlyList<string> OrderedResourceTypes { get; }

    /// <summary>
    /// Fetches one resource stream for a repository. The returned events are a
    /// complete batch for this pass; NextCursor is only safe to persist after
    /// every event has been ingested successfully.
    /// </summary>
    Task<ResourceFetchResult> FetchAsync(ResourceFetchRequest request, CancellationToken cancellationToken = default);
}

public sealed record ResourceFetchRequest(
    string RepositoryKey,
    string ResourceType,
    string? Cursor,
    int InitialLookbackDays,
    DateTimeOffset Now);

public sealed record ResourceFetchResult(
    IReadOnlyList<TracebackEvent> Events,
    string? NextCursor)
{
    /// <summary>External objects examined while producing this batch (pages walked included).</summary>
    public required int InspectedCount { get; init; }
}
