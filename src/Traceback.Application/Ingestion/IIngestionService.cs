using Traceback.Connectors.Abstractions;

namespace Traceback.Application.Ingestion;

/// <summary>Outcome of an ingestion batch.</summary>
public sealed record IngestionResult(
    int Received,
    int Applied,
    int Duplicated);

/// <summary>
/// The single entry point through which every provider fact enters Traceback.
/// Implementations guarantee idempotency (duplicate events are ignored), atomic
/// batch application, and provenance capture.
/// </summary>
public interface IIngestionService
{
    Task<IngestionResult> IngestAsync(IEnumerable<TracebackEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>A single resource stream within one synchronization run.</summary>
public sealed record ResourceSyncOutcome(
    string ResourceType,
    int Inspected,
    int ObservationsReceived,
    int ObservationsApplied,
    int Duplicated,
    double DurationMs,
    string? Cursor,
    bool CursorAdvanced,
    string? Error = null)
{
    public bool Success => Error is null;
}

/// <summary>Aggregate outcome of synchronizing one repository across all resource streams.</summary>
public sealed record RepositorySyncResult(
    string Provider,
    string RepositoryKey,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Success,
    string? Error,
    IReadOnlyList<ResourceSyncOutcome> Resources)
{
    public int TotalInspected => Resources.Sum(r => r.Inspected);
    public int TotalObservationsReceived => Resources.Sum(r => r.ObservationsReceived);
    public int TotalObservationsApplied => Resources.Sum(r => r.ObservationsApplied);
    public int TotalDuplicates => Resources.Sum(r => r.Duplicated);
}

public sealed record RepositorySyncRequest(string RepositoryKey, int InitialLookbackDays = 30, CancellationToken CancellationToken = default);

/// <summary>
/// Orchestrates an incremental synchronization pass for one repository:
/// fetches each configured resource stream through the provider's
/// <see cref="IRepositorySyncSource"/>, ingests it atomically, and advances
/// that stream's checkpoint only after its events are durably stored. A failed
/// stream stops the run without advancing past missing data.
/// </summary>
public interface IRepositorySynchronizer
{
    Task<RepositorySyncResult> SynchronizeAsync(string provider, RepositorySyncRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Checkpoint state of one integration resource stream, safe to expose.</summary>
public sealed record SyncStateView(
    string IntegrationId,
    string ResourceType,
    string? Cursor,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset LastAttemptAt,
    string? LastError,
    DateTimeOffset UpdatedAt);

public interface ISyncStateQueries
{
    /// <summary>All known synchronization checkpoints, optionally filtered by provider.</summary>
    Task<IReadOnlyList<SyncStateView>> GetStatesAsync(string? provider = null, CancellationToken cancellationToken = default);
}
