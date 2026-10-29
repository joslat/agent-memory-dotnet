using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>
/// Stored text in one normal form (pack format v1.2 §6.1, review 5): a record's key, its string properties, its link targets and
/// the keys it replaces in Unicode NFC, by the format's own Unicode 15 tables. Producers bring what they read into it (a snapshot
/// as it is built, a provider's proposals as they are received); admission refuses what is still not in it. A walk takes its
/// anchors in NFC, so a record written in another form could never be reached (review 5: a code map imported from a
/// repository with decomposed file names), while the same records in a pack, whose JSON profile requires NFC, could.
/// </summary>
[Experimental("AMEXT001")]
internal static class ModuleText
{
    /// <summary>Whether the text is in NFC; null is nothing to check.</summary>
    internal static bool IsNfc(string? value) => value is null || Unicode.Nfc.IsNormalized(value);

    /// <summary>The proposal with every text it stores in NFC; the same instance when nothing changes.</summary>
    internal static ModuleProposal InNfc(ModuleProposal proposal)
    {
        if (proposal is null) return proposal!;
        var changed = false;
        string Text(string value)
        {
            var nfc = Unicode.Nfc.Normalize(value) ?? value; // an unpaired surrogate has no NFC: admission refuses it
            if (!ReferenceEquals(nfc, value)) changed = true;
            return nfc;
        }
        var key = proposal.Key is null ? null : Text(proposal.Key);
        var properties = proposal.Properties?.ToDictionary(p => p.Key, p =>
            p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } text && Text(text) is var nfc && !ReferenceEquals(nfc, text)
                ? JsonSerializer.SerializeToElement(nfc) : p.Value, StringComparer.Ordinal);
        List<ProposedLink>? links = proposal.Links?.Select(l => l is null || l.Target is null ? l! : Text(l.Target) is var target && !ReferenceEquals(target, l.Target) ? l with { Target = target } : l).ToList();
        List<string>? replaces = proposal.Replaces?.Select(r => r is null ? r! : Text(r)).ToList();
        if (!changed) return proposal;
        return proposal with
        {
            Key = key!, Properties = properties!.AsReadOnly(), Links = links!.AsReadOnly(), Replaces = replaces!.AsReadOnly(),
        };
    }

    /// <summary>Every proposal with its text in NFC.</summary>
    internal static IReadOnlyList<ModuleProposal> InNfc(IReadOnlyList<ModuleProposal> proposals) =>
        proposals is null ? proposals! : proposals.Select(InNfc).ToList().AsReadOnly();
}
