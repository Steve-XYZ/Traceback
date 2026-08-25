namespace Traceback.Domain.Entities;

public sealed class WorkflowRun : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Provider-scoped display identity, e.g. "acme/player-manager/actions/runs/98122/attempts/2".</summary>
    public string ExternalName { get; set; } = null!;

    /// <summary>Repository the run executed in. Runs belong to exactly one repository.</summary>
    public Guid? SourceRepositoryId { get; set; }
    public SourceRepository? SourceRepository { get; set; }

    /// <summary>Display form of the owning repository.</summary>
    public string? Repository { get; set; }

    /// <summary>Provider's stable run identifier (GitHub: numeric run id). Shared by all attempts of a rerun.</summary>
    public long? RunId { get; set; }

    /// <summary>1-based attempt number. Reruns create new attempts; each keeps its own historical row.</summary>
    public int? RunAttempt { get; set; }

    public string? WorkflowName { get; set; }
    public long? RunNumber { get; set; }

    /// <summary>The event that triggered the run, e.g. push, pull_request, schedule.</summary>
    public string? TriggerEvent { get; set; }
    /// <summary>Branch the run executed against, when reported.</summary>
    public string? Branch { get; set; }
    public string? Url { get; set; }

    /// <summary>queued | in_progress | completed, ...</summary>
    public string? Status { get; set; }
    /// <summary>success | failure | cancelled | skipped | neutral | timed_out, ...</summary>
    public string? Conclusion { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Freshest provider state timestamp projected onto this row. Gates scalar
    /// overwrites so late deliveries of an older status cannot hide that the
    /// run has since completed; see StateFreshnessPolicy.
    /// </summary>
    public DateTimeOffset? ProviderStateAt { get; set; }

    /// <summary>Commit checked out by this run. Nullable: the run may be observed before its commit.</summary>
    public Guid? CommitId { get; set; }
    public Commit? Commit { get; set; }

    public List<WorkflowRunArtifact> Produces { get; set; } = [];
}
