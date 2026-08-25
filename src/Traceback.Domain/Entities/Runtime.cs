namespace Traceback.Domain.Entities;

/// <summary>Lifecycle of a deployment as reported by the source system.</summary>
public enum DeploymentStatus
{
    /// <summary>Source did not state an outcome.</summary>
    Unknown = 0,
    InProgress = 1,
    Succeeded = 2,
    Failed = 3,
}

public sealed class Service : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Canonical, lowercased service name. Unique.</summary>
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? Team { get; set; }

    public List<ServiceInstance> Instances { get; set; } = [];
}

/// <summary>A named runtime environment (staging, production, ...). Unique by name. CLR type avoids the System.Environment clash.</summary>
public sealed class DeploymentEnvironment : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Canonical, lowercased environment name. Unique.</summary>
    public string Name { get; set; } = null!;
    public string? Kind { get; set; }

    public List<ServiceInstance> Instances { get; set; } = [];
}

public sealed class ServiceInstance : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Provider-scoped display identity, e.g. "player-manager-7d9f4b-pod1".</summary>
    public string ExternalName { get; set; } = null!;
    public string? Hostname { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }

    public Guid ServiceId { get; set; }
    public Service Service { get; set; } = null!;

    public Guid EnvironmentId { get; set; }
    public DeploymentEnvironment Environment { get; set; } = null!;
}
