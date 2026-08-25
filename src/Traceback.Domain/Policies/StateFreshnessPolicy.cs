using Traceback.Domain.Entities;

namespace Traceback.Domain.Policies;

/// <summary>
/// Freshness gate for provider-reported mutable state. Providers redeliver old
/// representations (overlap windows, retries, out-of-order streams); a stale
/// representation must never overwrite newer provider state merely because it
/// arrived later. The rule:
///
/// - an observation with a known state timestamp applies its scalars when its
///   timestamp is not older than the freshest state already projected;
/// - observations without a state timestamp apply as before (non-null wins) —
///   connectors that cannot know their freshness opt out explicitly;
/// - relationship edges are always additive and never gated by freshness;
/// - every accepted observation still appends to the evidence log regardless.
/// </summary>
public static class StateFreshnessPolicy
{
    /// <summary>
    /// True when <paramref name="incomingStateUpdatedAt"/> may overwrite scalar
    /// state on an entity whose freshest projected provider state is
    /// <paramref name="currentProviderStateAt"/>.
    /// </summary>
    public static bool CanApplyScalars(DateTimeOffset? currentProviderStateAt, DateTimeOffset? incomingStateUpdatedAt)
    {
        if (currentProviderStateAt is null)
            return true;
        if (incomingStateUpdatedAt is null)
            return true;
        return incomingStateUpdatedAt.Value >= currentProviderStateAt.Value;
    }

    /// <summary>Newest-known provider state timestamp after applying an observation.</summary>
    public static DateTimeOffset Merge(DateTimeOffset current, DateTimeOffset? incomingStateUpdatedAt) =>
        incomingStateUpdatedAt is { } t && t > current ? t : current;
}
