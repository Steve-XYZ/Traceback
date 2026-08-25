using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Traceback.Application.Ingestion;
using Traceback.Connectors.Abstractions;
using Traceback.Infrastructure.Persistence;

namespace Traceback.Infrastructure.Ingestion;

/// <summary>
/// Transactional, idempotent ingestion pipeline. Each batch runs in one database
/// transaction; events already recorded (by fingerprint) are skipped; every
/// applied event is appended to the observations log before its domain effects.
///
/// Events flush to the database in chunks instead of one round trip per event:
/// resolvers and edge checks consult per-batch memo state first, so in-batch
/// references resolve without an intermediate save. The enclosing transaction
/// keeps the whole batch atomic regardless of chunk boundaries.
/// </summary>
public sealed partial class IngestionService(TracebackDbContext db, ILogger<IngestionService> logger) : IIngestionService
{
    internal const int SaveChunkSize = 200;

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion batch failed after {Applied} applied events; rolled back")]
    private partial void LogBatchFailed(int applied, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingested batch: {Received} received, {Applied} applied, {Duplicated} duplicates in {DurationMs:F1} ms")]
    private partial void LogBatchCompleted(int received, int applied, int duplicated, double durationMs);

    internal static readonly ActivitySource Activity = new("Traceback.Ingestion");
    internal static readonly Meter Meter = new("Traceback.Ingestion");

    private static readonly Counter<long> EventsReceived =
        Meter.CreateCounter<long>("traceback.ingestion.events_received", description: "Normalized events received by the ingestion pipeline.");
    private static readonly Counter<long> EventsApplied =
        Meter.CreateCounter<long>("traceback.ingestion.events_applied", description: "Events newly applied to domain state.");
    private static readonly Counter<long> EventsDuplicated =
        Meter.CreateCounter<long>("traceback.ingestion.events_duplicated", description: "Events skipped as duplicate deliveries.");
    private static readonly Histogram<double> BatchDuration =
        Meter.CreateHistogram<double>("traceback.ingestion.batch.duration", unit: "ms", description: "End-to-end ingestion batch duration.");

    public async Task<IngestionResult> IngestAsync(IEnumerable<TracebackEvent> events, CancellationToken cancellationToken = default)
    {
        var batch = (events ?? throw new ArgumentNullException(nameof(events))).ToList();
        using var activity = Activity.StartActivity("traceback.ingest");
        activity?.SetTag("traceback.events.count", batch.Count);

        var stopwatch = Stopwatch.StartNew();
        var received = 0;
        var applied = 0;
        var duplicated = 0;

        if (batch.Count == 0)
            return new IngestionResult(0, 0, 0);

        // Serialize once: fingerprints and stored payloads share canonical JSON.
        var serialized = new List<(TracebackEvent Event, string EventType, string EntityType, string ExternalKey, string PayloadJson, string Fingerprint)>(batch.Count);
        var seenInBatch = new HashSet<string>();
        foreach (var evt in batch)
        {
            received++;
            EventsReceived.Add(1, new KeyValuePair<string, object?>("event.type", evt.GetType().Name));

            var payloadJson = ObservationFingerprint.Serialize(evt);
            var fingerprint = ObservationFingerprint.Compute(evt.Provenance.Provider, evt.GetType().Name, evt);
            if (!seenInBatch.Add(fingerprint))
            {
                duplicated++;
                EventsDuplicated.Add(1, new KeyValuePair<string, object?>("event.type", evt.GetType().Name));
                continue;
            }
            serialized.Add((evt, evt.GetType().Name, evt.Provenance.EntityType, evt.Provenance.ExternalKey, payloadJson, fingerprint));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Skip anything already ingested (same content delivered again),
            // probing known fingerprints in chunks to bound query size.
            var known = new HashSet<string>();
            foreach (var chunk in Chunk(serialized.Select(s => s.Fingerprint).ToList(), 1000))
            {
                var found = await db.Observations
                    .Where(o => chunk.Contains(o.Fingerprint))
                    .Select(o => o.Fingerprint)
                    .ToListAsync(cancellationToken);
                known.UnionWith(found);
            }

            var applier = new IngestionApplier(db, new EntityResolver(db));

            var sinceLastSave = 0;
            foreach (var entry in serialized)
            {
                if (known.Contains(entry.Fingerprint))
                {
                    duplicated++;
                    EventsDuplicated.Add(1, new KeyValuePair<string, object?>("event.type", entry.EventType));
                    continue;
                }

                using var applyActivity = Activity.StartActivity("apply-event");
                applyActivity?.SetTag("traceback.event.type", entry.EventType);
                applyActivity?.SetTag("traceback.event.provider", entry.Event.Provenance.Provider);
                applyActivity?.SetTag("traceback.event.external_key", entry.ExternalKey);

                var observation = new Observation
                {
                    Provider = entry.Event.Provenance.Provider,
                    EventType = entry.EventType,
                    EntityTypeName = entry.EntityType,
                    ExternalKey = entry.ExternalKey,
                    OccurredAt = entry.Event.Provenance.OccurredAt,
                    ObservedAt = entry.Event.Provenance.ObservedAt,
                    Fingerprint = entry.Fingerprint,
                    PayloadJson = entry.PayloadJson,
                };
                await db.Observations.AddAsync(observation, cancellationToken);
                applier.CurrentObservation = observation;

                await applier.ApplyAsync(entry.Event, cancellationToken);

                applied++;
                EventsApplied.Add(1, new KeyValuePair<string, object?>("event.type", entry.EventType));

                // Flush in chunks; memo caches keep in-batch resolution correct.
                if (++sinceLastSave >= SaveChunkSize && db.ChangeTracker.HasChanges())
                {
                    await db.SaveChangesAsync(cancellationToken);
                    sinceLastSave = 0;
                }
            }

            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(cancellationToken);

            // Backfill sequence provenance now that observations have sequences.
            foreach (var (deployment, observation) in applier.PendingDeployments)
                deployment.IngestedSequence = observation.Sequence;
            foreach (var (edge, observation) in applier.PendingWorkItemPullRequests)
                edge.EstablishedSequence = observation.Sequence;
            foreach (var (edge, observation) in applier.PendingPullRequestCommits)
                edge.EstablishedSequence = observation.Sequence;
            foreach (var (edge, observation) in applier.PendingWorkflowRunArtifacts)
                edge.EstablishedSequence = observation.Sequence;

            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogBatchFailed(applied, ex);
            throw;
        }

        stopwatch.Stop();
        activity?.SetTag("traceback.events.applied", applied);
        activity?.SetTag("traceback.events.duplicated", duplicated);
        BatchDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        LogBatchCompleted(received, applied, duplicated, stopwatch.Elapsed.TotalMilliseconds);

        return new IngestionResult(received, applied, duplicated);
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}
