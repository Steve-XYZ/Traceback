namespace Traceback.Infrastructure.Persistence;

/// <summary>
/// Synchronization checkpoint for one integration's resource stream (e.g.
/// github/acme/player-manager + pull_requests). The cursor is an opaque,
/// provider-defined watermark; it is persisted only after the corresponding
/// events have been ingested successfully, so a failed or partial pass never
/// advances past missing data and the next synchronization resumes safely.
/// </summary>
public sealed class SyncState
{
    /// <summary>e.g. "github/acme/player-manager".</summary>
    public required string IntegrationId { get; init; }

    /// <summary>Resource stream name, e.g. "pull_requests", "workflow_runs".</summary>
    public required string ResourceType { get; init; }

    /// <summary>Opaque resume watermark; null until the first successful pass.</summary>
    public string? Cursor { get; set; }

    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
