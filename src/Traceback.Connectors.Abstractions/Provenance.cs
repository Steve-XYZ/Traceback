namespace Traceback.Connectors.Abstractions;

/// <summary>
/// Provenance attached to every normalized event: who reported the fact, which
/// external object it describes, when it happened in the source system, and when
/// Traceback observed it.
/// </summary>
public sealed record EventProvenance(
    string Provider,
    string EntityType,
    string ExternalKey,
    string? ExternalUrl,
    DateTimeOffset OccurredAt,
    DateTimeOffset ObservedAt)
{
    /// <summary>ObservedAt defaulting helper for connectors without a receive timestamp.</summary>
    public static DateTimeOffset DefaultObservedAt => DateTimeOffset.UtcNow;
}

/// <summary>
/// A typed reference from one observed entity to another, expressed purely in
/// provider/external-key terms. The ingestion pipeline resolves these against the
/// identity mapping; unresolved references create placeholder entities.
/// </summary>
public sealed record ExternalRef(string Provider, string EntityType, string ExternalKey)
{
    public override string ToString() => $"{Provider}/{EntityType}/{ExternalKey}";
}
