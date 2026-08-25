namespace Traceback.Domain.Entities;

/// <summary>
/// Base contract for every entity that originates from an external system.
/// Carries observation bookkeeping; provider-specific identifiers live in the
/// identity mapping (Infrastructure), not on domain entities.
/// </summary>
public interface IExternallySourced
{
    /// <summary>Internal surrogate identifier. Never derived from a provider.</summary>
    Guid Id { get; set; }

    /// <summary>Provider that first created this record in Traceback.</summary>
    string CreatedByProvider { get; set; }

    /// <summary>Earliest time Traceback observed any fact about this entity (UTC).</summary>
    DateTimeOffset FirstObservedAt { get; set; }

    /// <summary>Latest time Traceback observed an update for this entity (UTC).</summary>
    DateTimeOffset LastObservedAt { get; set; }

    /// <summary>
    /// True when this row was created only because another entity referenced it
    /// before its own observation arrived. Placeholder rows carry identifiers but
    /// few or no attributes, and are filled in when the real observation lands.
    /// </summary>
    bool IsPlaceholder { get; set; }
}
