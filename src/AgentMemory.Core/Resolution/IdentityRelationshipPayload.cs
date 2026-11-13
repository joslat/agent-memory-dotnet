using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Resolution;

/// <summary>Bounded, frozen original relationship data; model attributes cannot execute host getters.</summary>
internal static class IdentityRelationshipPayload
{
    internal const int MaxBytes = 65_536;
    internal const int MaxObservations = 256;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    internal static IdentityRelationshipObservation Capture(IdentityRelationshipObservation input, string? ownerId)
    {
        ArgumentNullException.ThrowIfNull(input);
        Text(input.ObservationId);
        if (ownerId is not null) Text(ownerId);
        if (input.OwnerId != ownerId) throw new ArgumentException("A relationship observation needs its exact write owner.");
        var original = input.Observation ?? throw new ArgumentException("A relationship needs its original assertion.");
        Text(original.SourceEntity); Text(original.TargetEntity); Text(original.RelationshipType);
        if (original.ReplacesId is not null) Text(original.ReplacesId);
        if (original.Attributes is null || !double.IsFinite(original.Confidence) || original.Confidence is < 0 or > 1 || original.Description?.Length > MaxBytes ||
            input.SourceMessageIds is null || input.SourceMessageIds.Count is 0 or > 256)
            throw new ArgumentException("A relationship assertion needs bounded provenance and finite confidence.");
        foreach (var source in input.SourceMessageIds) Text(source);
        if (input.SourceDecisionId is not null) Text(input.SourceDecisionId);
        if (input.TargetDecisionId is not null) Text(input.TargetDecisionId);
        IdentityDecisionPayload.CheckData(original.Attributes, 0);
        var normalized = input with { CreatedAtUtc = input.CreatedAtUtc.ToUniversalTime(),
            EndedAtUtc = input.EndedAtUtc?.ToUniversalTime() };
        var bytes = IdentityDecisionPayload.Serialize(normalized);
        if (bytes.Length > MaxBytes) throw new ArgumentException("A relationship observation exceeds its UTF8 bound.");
        return JsonSerializer.Deserialize<IdentityRelationshipObservation>(bytes, Json)
            ?? throw new ArgumentException("Invalid relationship observation.");
    }

    internal static string Encode(IdentityRelationshipObservation observation) => Encoding.UTF8.GetString(IdentityDecisionPayload.Serialize(observation));

    internal static IdentityRelationshipObservation Decode(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaxBytes) throw new InvalidOperationException("Stored relationship observation exceeds its bound.");
        return JsonSerializer.Deserialize<IdentityRelationshipObservation>(value, Json)
            ?? throw new InvalidOperationException("Invalid stored relationship observation.");
    }

    // Source provenance and original words determine the claim. Runtime identities, confidence
    // variation and replay time cannot silently replace that original claim or its endpoint support.
    internal static string Fingerprint(IdentityRelationshipObservation value) => Convert.ToHexString(SHA256.HashData(
        IdentityDecisionPayload.Serialize(new
        {
            value.OwnerId,
            roots = value.SourceMessageIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            value.Observation.SourceEntity, value.Observation.TargetEntity, value.Observation.RelationshipType,
            value.Observation.Description, value.Observation.ReplacesId,
            attributes = value.Observation.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, Value = Canonical(pair.Value) }).ToArray()
        })));

    private static object? Canonical(object? value) => value is JsonElement element ? element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(property => property.Name, property => Canonical(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(item => Canonical(item)).ToArray(),
        _ => element.Clone()
    } : value;

    private static void Text(string? value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("Identity relationship text needs 1–4096 characters."); }
}
