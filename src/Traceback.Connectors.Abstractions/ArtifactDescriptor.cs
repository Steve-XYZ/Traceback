namespace Traceback.Connectors.Abstractions;

/// <summary>
/// Describes a buildable artifact (typically a container image or CI output)
/// referenced by workflow-run or deployment events. Identity precedence:
/// provider-reported digest (global content identity) first, then ExternalKey
/// (provider-stable id), then name@version in that provider's namespace. A
/// digest is source metadata and is not a container-image assertion. The
/// canonical key hint lets providers whose artifacts carry no digest or version
/// (e.g. GitHub Actions artifacts) register a stable provider-scoped key
/// instead of a guessable name; the resolver retains that raw key as a scoped
/// identity and namespaces its persisted canonical fallback.
/// </summary>
public sealed record ArtifactDescriptor(
    string Name,
    string? Version,
    string? Digest,
    string? Uri,
    string? CanonicalKeyHint = null)
{
    public override string ToString() => Digest is not null ? $"{Name}@{Digest}" : $"{Name}:{Version}";
}
