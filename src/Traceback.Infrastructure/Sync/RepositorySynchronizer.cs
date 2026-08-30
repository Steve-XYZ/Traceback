using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Traceback.Application.Ingestion;
using Traceback.Connectors.Abstractions;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Sync;

/// <summary>
/// Orchestrates one repository synchronization pass across a provider's
/// resource streams. Checkpoint discipline:
///
/// - each resource stream is fetched completely, then ingested atomically
///   (single transaction), and only then is its cursor persisted;
/// - a failure anywhere stops the run: earlier streams stay advanced (their
///   data is durably ingested), the failing stream keeps its previous cursor
///   so the next synchronization resumes without skipping missing data;
/// - cursors live in PostgreSQL (sync_states), so progress survives restarts.
/// </summary>
public sealed partial class RepositorySynchronizer(
    IEnumerable<IRepositorySyncSource> sources,
    IIngestionService ingestion,
    TracebackDbContext db,
    ILogger<RepositorySynchronizer> logger) : IRepositorySynchronizer
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Sync {IntegrationId}: {ResourceType} inspected {Inspected}, received {Received}, applied {Applied}, duplicates {Duplicated}")]
    private partial void LogResourceCompleted(string integrationId, string resourceType, int inspected, int received, int applied, int duplicated);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync {IntegrationId}: {ResourceType} failed; cursor not advanced")]
    private partial void LogResourceFailed(string integrationId, string resourceType, Exception ex);

    internal static readonly ActivitySource Activity = new("Traceback.Sync");
    internal static readonly Meter Meter = new("Traceback.Sync");

    private static readonly Counter<long> SyncFailures =
        Meter.CreateCounter<long>("traceback.sync.failures", description: "Synchronization passes that ended in failure.");
    private static readonly Histogram<double> SyncDuration =
        Meter.CreateHistogram<double>("traceback.sync.duration", unit: "ms", description: "End-to-end repository synchronization duration.");
    private static readonly Counter<long> ObservationsApplied =
        Meter.CreateCounter<long>("traceback.sync.observations_applied", description: "Observations newly applied during synchronization.");
    private static readonly Counter<long> ObservationsDuplicated =
        Meter.CreateCounter<long>("traceback.sync.observations_duplicated", description: "Duplicate observations skipped during synchronization.");

    public async Task<RepositorySyncResult> SynchronizeAsync(string provider, RepositorySyncRequest request, CancellationToken cancellationToken = default)
    {
        var source = sources.FirstOrDefault(s => s.Provider == provider)
            ?? throw new InvalidOperationException($"No synchronization source registered for provider '{provider}'.");

        var repositoryKey = request.RepositoryKey.Trim().ToLowerInvariant();
        var integrationId = $"{provider}/{repositoryKey}";
        var startedAt = DateTimeOffset.UtcNow;
        var ct = request.CancellationToken;

        using var activity = Activity.StartActivity($"{provider}.sync");
        activity?.SetTag("traceback.sync.integration_id", integrationId);
        activity?.SetTag("traceback.sync.repository", repositoryKey);
        var stopwatch = Stopwatch.StartNew();

        var outcomes = new List<ResourceSyncOutcome>();
        string? error = null;

        foreach (var resourceType in source.OrderedResourceTypes)
        {
            var outcome = await SynchronizeResourceAsync(source, integrationId, request, resourceType, ct);
            outcomes.Add(outcome);

            ObservationsApplied.Add(outcome.ObservationsApplied, new KeyValuePair<string, object?>("repository", repositoryKey));
            ObservationsDuplicated.Add(outcome.Duplicated, new KeyValuePair<string, object?>("repository", repositoryKey));

            if (!outcome.Success)
            {
                error = $"{resourceType}: {outcome.Error}";
                activity?.SetStatus(ActivityStatusCode.Error, error);
                SyncFailures.Add(1, new KeyValuePair<string, object?>("integration", integrationId));
                LogResourceFailed(integrationId, resourceType, new InvalidOperationException(error));
                break;
            }

            LogResourceCompleted(integrationId, resourceType, outcome.Inspected, outcome.ObservationsReceived, outcome.ObservationsApplied, outcome.Duplicated);
        }

        stopwatch.Stop();
        SyncDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("repository", repositoryKey));

        return new RepositorySyncResult(provider, repositoryKey, startedAt, DateTimeOffset.UtcNow, error is null, error, outcomes);
    }

    private async Task<ResourceSyncOutcome> SynchronizeResourceAsync(
        IRepositorySyncSource source, string integrationId, RepositorySyncRequest request, string resourceType, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var state = await db.SyncStates.FindAsync([integrationId, resourceType], ct);
        if (state is null)
        {
            state = new SyncState
            {
                IntegrationId = integrationId,
                ResourceType = resourceType,
                LastAttemptAt = now,
                UpdatedAt = now,
            };
            await db.SyncStates.AddAsync(state, ct);
        }
        state.LastAttemptAt = now;
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var resourceStopwatch = Stopwatch.StartNew();
        try
        {
            using var fetchSpan = Activity.StartActivity($"{source.Provider}.fetch.{resourceType}");
            fetchSpan?.SetTag("traceback.sync.repository", request.RepositoryKey);

            var fetch = await source.FetchAsync(
                new ResourceFetchRequest(request.RepositoryKey, resourceType, state.Cursor, request.InitialLookbackDays, now),
                ct);

            var ingest = await ingestion.IngestAsync(fetch.Events, ct);
            resourceStopwatch.Stop();

            // Checkpoint boundary: the cursor advances only after this
            // stream's events are durably committed.
            var advanced = !string.Equals(fetch.NextCursor, state.Cursor, StringComparison.Ordinal);
            if (fetch.NextCursor is not null)
                state.Cursor = fetch.NextCursor;
            state.LastSuccessAt = DateTimeOffset.UtcNow;
            state.LastError = null;
            state.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            fetchSpan?.SetTag("traceback.sync.inspected", fetch.InspectedCount);
            fetchSpan?.SetTag("traceback.sync.applied", ingest.Applied);

            var cursor = state.Cursor;

            // Each stream starts from a clean tracker: the previous stream's
            // committed graph is of no use to the next one and only slows
            // change detection down as the pass grows.
            db.ChangeTracker.Clear();

            return new ResourceSyncOutcome(
                resourceType,
                fetch.InspectedCount,
                ingest.Received,
                ingest.Applied,
                ingest.Duplicated,
                resourceStopwatch.Elapsed.TotalMilliseconds,
                cursor,
                advanced);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            resourceStopwatch.Stop();
            return await RecordFailureAsync(integrationId, resourceType, resourceStopwatch, ex, ct);
        }
    }

    /// <summary>
    /// Records a stream failure without letting the failed batch reach the
    /// database. A rolled-back ingestion leaves its entities in the change
    /// tracker, so writing the checkpoint row on the same context would flush
    /// them - untransacted, and without the observations that justify them.
    /// The tracker is dropped first and the checkpoint reloaded.
    /// </summary>
    private async Task<ResourceSyncOutcome> RecordFailureAsync(
        string integrationId, string resourceType, Stopwatch resourceStopwatch, Exception ex, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var state = await db.SyncStates.FindAsync([integrationId, resourceType], ct);
        if (state is null)
        {
            state = new SyncState
            {
                IntegrationId = integrationId,
                ResourceType = resourceType,
                LastAttemptAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await db.SyncStates.AddAsync(state, ct);
        }

        state.LastError = SanitizeError(ex);
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var cursor = state.Cursor;
        db.ChangeTracker.Clear();

        return new ResourceSyncOutcome(
            resourceType,
            Inspected: 0,
            ObservationsReceived: 0,
            ObservationsApplied: 0,
            Duplicated: 0,
            DurationMs: resourceStopwatch.Elapsed.TotalMilliseconds,
            Cursor: cursor,
            CursorAdvanced: false,
            Error: SanitizeError(ex));
    }

    /// <summary>Single-line, message-only error text: exception data may carry provider payloads, so it is dropped.</summary>
    private static string SanitizeError(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ").Trim();
        return message.Length > 512 ? message[..512] : message;
    }
}
