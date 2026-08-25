using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Traceback.Connectors.Abstractions;

namespace Traceback.Infrastructure.Ingestion;

/// <summary>
/// Content fingerprint over schema version, provider, event type, and canonical
/// event JSON. Two events with identical fingerprints are treated as the same
/// delivery and ignored on repeat receipt.
///
/// <see cref="EventProvenance.ObservedAt"/> is excluded from the material: it is
/// receipt metadata that changes on every fetch, so including it would make
/// unchanged redeliveries (overlap windows) look like new content. Provider
/// facts such as <see cref="EventProvenance.OccurredAt"/> stay in the material,
/// so genuinely updated objects still fingerprint differently.
/// </summary>
public static class ObservationFingerprint
{
    private const string SchemaVersion = "tb.v1";

    public static string Compute(string provider, string eventType, TracebackEvent evt)
    {
        // Receipt time is not part of delivery identity; neutralize it before hashing.
        var canonical = evt with { Provenance = evt.Provenance with { ObservedAt = default } };
        return Compute(provider, eventType, Serialize(canonical));
    }

    private static string Compute(string provider, string eventType, string payloadJson)
    {
        var material = $"{SchemaVersion}|{provider}|{eventType}|{payloadJson}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    /// <summary>Canonical serialization used both for fingerprinting and payload storage.</summary>
    public static string Serialize(object evt) =>
        JsonSerializer.Serialize(evt, Persistence.TracebackDbContext.PayloadSerializerOptions);
}
