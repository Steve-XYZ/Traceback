namespace Traceback.Domain.Entities;

public sealed class BuildArtifact : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>e.g. "player-manager" (image/repository name).</summary>
    public string Name { get; set; } = null!;

    /// <summary>Mutable version label, e.g. a tag like "be82d".</summary>
    public string? Version { get; set; }

    /// <summary>
    /// Provider-reported content digest, e.g. "sha256:...". This is not a
    /// container-image digest unless a provider separately proves that fact.
    /// Immutable when present.
    /// </summary>
    public string? Digest { get; set; }

    /// <summary>
    /// Stable resolution key used to correlate artifact references: the
    /// provider-reported digest when known, otherwise a provider-qualified
    /// form of the first provider key or "name@version" seen. The raw
    /// provider key remains in ExternalIdentity; digest identities may
    /// correlate across providers. Legacy rows with an unqualified key remain
    /// resolvable within their owning provider.
    /// </summary>
    public string CanonicalKey { get; set; } = null!;

    public string? Uri { get; set; }

    public List<WorkflowRunArtifact> ProducedBy { get; set; } = [];
    public List<Deployment> DeployedAs { get; set; } = [];
}
