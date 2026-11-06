// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>
/// One rule of a pack's usage policy (feature 10): for an audience (<c>*</c>, or WebIDs), the verbs it allows and denies, and
/// optionally when its grants end.
/// </summary>
internal sealed record PackPolicyRule(IReadOnlyList<string> Audience, IReadOnlySet<string> Allow, IReadOnlySet<string> Deny, DateTimeOffset? NotAfter)
{
    /// <summary>Whether the rule speaks to a host acting as these WebIDs: it names everyone, or one of them.</summary>
    internal bool AppliesTo(IReadOnlyCollection<string> identities) => Audience.Contains(PackPolicy.Everyone) || Audience.Any(identities.Contains);
}

/// <summary>
/// What a receiver may do with a pack (feature 10, R-CORE-138): <c>policy.json</c>, inside the signed content so it is part of the
/// pack's identity, canonical (RFC 8785) and strict, read alike by both verifiers. A host asks <see cref="Allows"/> at the door
/// where each verb's action happens.
/// </summary>
internal sealed record PackPolicy(IReadOnlyList<PackPolicyRule> Rules)
{
    internal static readonly string FileName = PackRegistry.PolicyFile;
    internal const string Everyone = "*";
    internal const int MaximumRules = 64;
    internal const int MaximumAudience = 16;

    /// <summary>
    /// The one rule every door asks: a verb is allowed when a rule for this host allows it and has not ended, and no rule for this
    /// host denies it. Deny wins, and a rule's denials outlive its grants (a past notAfter ends what it allowed, never what it
    /// refused).
    /// </summary>
    internal bool Allows(string verb, IReadOnlyCollection<string> identities, DateTimeOffset now)
    {
        var applying = Rules.Where(r => r.AppliesTo(identities)).ToList();
        return !applying.Any(r => r.Deny.Contains(verb)) && applying.Any(r => r.Allow.Contains(verb) && (r.NotAfter is not { } end || now <= end));
    }

    /// <summary>Reads a policy strictly, or returns why it is not one.</summary>
    internal static (PackPolicy? Policy, string? Problem) Parse(byte[] bytes)
    {
        if (bytes.Length > PackVerifier.MaximumJsonBytes) return (null, $"{FileName} is larger than {PackVerifier.MaximumJsonBytes >> 20} MiB.");
        if (PackJson.Check(bytes, integersOnly: true) is { } malformed) return (null, $"{FileName} {malformed}.");
        if (!Jcs.Canonical(bytes).AsSpan().SequenceEqual(bytes)) return (null, $"{FileName} is not canonical (RFC 8785).");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !PackRegistry.Shape("policy").Contains(p.Name)))
            return (null, $"{FileName} holds other fields than a usage policy.");
        if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != PackRegistry.PolicyFormat)
            return (null, $"{FileName} must name format '{PackRegistry.PolicyFormat}'.");
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() is 0 or > MaximumRules)
            return (null, $"{FileName} holds 1 to {MaximumRules} rules.");
        var read = new List<PackPolicyRule>();
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || rule.EnumerateObject().Any(p => !PackRegistry.Shape("policyRule").Contains(p.Name)))
                return (null, $"{FileName}: a rule holds an audience, and allow, deny and notAfter only.");
            if (!rule.TryGetProperty("audience", out var audience) || audience.ValueKind != JsonValueKind.Array || audience.GetArrayLength() is 0 or > MaximumAudience
                || audience.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String || a.GetString() != Everyone && !WebId.IsValid(a.GetString()))
                || audience.EnumerateArray().Select(a => a.GetString()).Distinct(StringComparer.Ordinal).Count() != audience.GetArrayLength())
                return (null, $"{FileName}: a rule's audience is 1 to {MaximumAudience} distinct entries, each '*' or a WebID.");
            if (Verbs(rule, "allow") is not { } allow || Verbs(rule, "deny") is not { } deny)
                return (null, $"{FileName}: allow and deny are distinct verbs, of {string.Join(", ", PackRegistry.PolicyVerbs.Order(StringComparer.Ordinal))}.");
            if (allow.Count == 0 && deny.Count == 0) return (null, $"{FileName}: a rule allows or denies at least one verb.");
            DateTimeOffset? notAfter = null;
            if (rule.TryGetProperty("notAfter", out var end))
            {
                if (end.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParseExact(end.GetString(), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
                    return (null, $"{FileName}: a rule's notAfter is an instant in UTC to the second.");
                notAfter = instant;
            }
            read.Add(new PackPolicyRule([.. audience.EnumerateArray().Select(a => a.GetString()!)], allow, deny, notAfter));
        }
        return (new PackPolicy(read), null);
    }

    // Absent is none; a list of distinct known verbs is itself; anything else is null.
    private static IReadOnlySet<string>? Verbs(JsonElement rule, string name)
    {
        if (!rule.TryGetProperty(name, out var value)) return new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || !PackRegistry.PolicyVerbs.Contains(v.GetString()!)))
            return null;
        var verbs = value.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal);
        return verbs.Count == value.GetArrayLength() ? verbs : null;
    }
}
