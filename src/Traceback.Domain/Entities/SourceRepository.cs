namespace Traceback.Domain.Entities;

/// <summary>
/// A source repository (e.g. GitHub "owner/name") that scopes every imported
/// engineering object. A pull request number, a workflow run id, or a branch is
/// only meaningful within one repository; this entity is the identity boundary
/// that keeps "owner-a/repo-x PR #42" distinct from "owner-b/repo-y PR #42".
/// </summary>
public sealed class SourceRepository : IExternallySourced
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CreatedByProvider { get; set; } = null!;
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    public bool IsPlaceholder { get; set; }

    /// <summary>Provider-scoped identity key, e.g. "acme/player-manager". Lowercased.</summary>
    public string Key { get; set; } = null!;

    /// <summary>Display form as the provider reports it, e.g. "Acme/Player-Manager".</summary>
    public string FullName { get; set; } = null!;
    public string? Owner { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Provider-native state: public, private, internal, ...</summary>
    public string? Visibility { get; set; }
    public string? DefaultBranch { get; set; }
    public string? Url { get; set; }

    /// <summary>Freshest provider state timestamp projected onto this row.</summary>
    public DateTimeOffset? ProviderStateAt { get; set; }
}
