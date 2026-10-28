using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The federated profile (federated packs §3): who publishes the pack, who it is for, and why, inside what is signed. It
/// travels as the <c>federation</c> extension of <c>pack.json</c> and must be marked critical, so a host that does not
/// implement it refuses the pack: an audience a host may ignore is not an audience.
/// </summary>
/// <param name="Publisher">The publisher's WebID; a counted publisher signature must come from a key bound to it.</param>
/// <param name="Audience">The WebIDs the pack is for; null when it is public.</param>
/// <param name="Purpose">Why it is shared: shown and recorded, not enforced.</param>
internal sealed record PackFederation(string Publisher, IReadOnlyList<string>? Audience, string? Purpose)
{
    internal const string ExtensionName = "federation";
    internal const string Format = "agentmemory.pack.federation/1";
    internal const string Public = "public";
    internal const int MaximumAudience = 64;
    internal const int MaximumPurpose = 300;

    internal bool IsPublic => Audience is null;

    /// <summary>Whether a host acting as any of <paramref name="hostWebIds"/> is in the audience.</summary>
    internal bool Admits(IEnumerable<string> hostWebIds) => IsPublic || hostWebIds.Any(Audience!.Contains);

    internal static string Describe(IReadOnlyList<string>? audience) => audience is null ? Public : string.Join(", ", audience);

    /// <summary>Reads the extension from <c>pack.json</c>'s <c>extensions</c> object; null when the pack is not federated.</summary>
    internal static (PackFederation? Federation, string? Problem) Read(JsonElement extensions)
    {
        if (!extensions.TryGetProperty(ExtensionName, out var value)) return (null, null);
        var critical = extensions.TryGetProperty("critical", out var names) && names.ValueKind == JsonValueKind.Array
            && names.EnumerateArray().Any(n => n.ValueKind == JsonValueKind.String && n.GetString() == ExtensionName);
        if (!critical) return (null, $"{PackManifest.FileName} carries the '{ExtensionName}' extension without marking it critical; a host that cannot enforce an audience must refuse the pack, not ignore it.");
        if (value.ValueKind != JsonValueKind.Object) return (null, $"{PackManifest.FileName} has a malformed '{ExtensionName}' extension: an object.");
        if (value.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n is not ("format" or "publisher" or "audience" or "purpose")) is { } unknown)
            return (null, $"The '{ExtensionName}' extension has an unknown field '{unknown}'.");
        if (!value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            return (null, $"The '{ExtensionName}' extension must name format '{Format}'.");
        if (!value.TryGetProperty("publisher", out var publisherValue) || publisherValue.ValueKind != JsonValueKind.String)
            return (null, $"The '{ExtensionName}' extension names no publisher.");
        var publisher = publisherValue.GetString()!;
        if (WebId.Problem(publisher, "The federation publisher") is { } publisherProblem) return (null, publisherProblem);
        if (!value.TryGetProperty("audience", out var audienceValue)) return (null, $"The '{ExtensionName}' extension names no audience: \"{Public}\" or a list of WebIDs.");
        var (audience, audienceProblem) = ReadAudience(audienceValue, "The federation audience");
        if (audienceProblem is not null) return (null, audienceProblem);
        string? purpose = null;
        if (value.TryGetProperty("purpose", out var purposeValue))
        {
            if (purposeValue.ValueKind != JsonValueKind.String || purposeValue.GetString()!.Length is 0 or > MaximumPurpose)
                return (null, $"The federation purpose is text of 1 to {MaximumPurpose} characters.");
            purpose = purposeValue.GetString();
        }
        return (new PackFederation(publisher, audience, purpose), null);
    }

    /// <summary>An audience: <c>"public"</c> (null), or 1 to 64 distinct WebIDs.</summary>
    internal static (IReadOnlyList<string>? Audience, string? Problem) ReadAudience(JsonElement value, string what)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() == Public) return (null, null);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaximumAudience
            || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
            return (null, $"{what} is \"{Public}\" or a list of 1 to {MaximumAudience} WebIDs.");
        var audience = value.EnumerateArray().Select(v => v.GetString()!).ToList();
        // The entry is named by position: an invalid one is untrusted text, never echoed.
        var invalid = audience.FindIndex(a => !WebId.IsValid(a));
        if (invalid >= 0) return (null, WebId.Problem(null, $"{what} entry {invalid + 1}"));
        if (audience.Distinct(StringComparer.Ordinal).Count() != audience.Count) return (null, $"{what} names a WebID twice.");
        return (audience, null);
    }
}
