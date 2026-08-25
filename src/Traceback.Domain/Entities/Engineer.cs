namespace Traceback.Domain.Entities;

public sealed class Engineer : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    public string DisplayName { get; set; } = null!;
    public string? Email { get; set; }
}
