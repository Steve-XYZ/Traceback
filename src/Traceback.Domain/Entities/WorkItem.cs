namespace Traceback.Domain.Entities;

public sealed class WorkItem : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Human-facing key, e.g. "BOS-2268". Unique when known.</summary>
    public string Key { get; set; } = null!;
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? Type { get; set; }
    public string? Url { get; set; }

    public Guid? AssigneeEngineerId { get; set; }
    public Engineer? Assignee { get; set; }

    public List<WorkItemPullRequest> ImplementedBy { get; set; } = [];
}
