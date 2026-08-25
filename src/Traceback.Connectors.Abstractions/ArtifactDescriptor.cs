namespace Traceback.Connectors.Abstractions;

/// <summary>
/// Describes a buildable artifact (typically a container image or CI output)
/// referenced by workflow-run or deployment events. Identity precedence:
/// digest (content-addressed) first, then ExternalKey (provider-stable id),
/// then name@version. The canonical key hint lets providers whose artifacts
/// carry no digest or version (e.g. GitHub Actions artifacts) register a
/// stable provider-scoped key instead of a guessable name.
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
