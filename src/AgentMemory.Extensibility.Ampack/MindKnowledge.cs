// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// The knowledge a Mind is bound to (<c>agentmemory.mind/2.0</c> §5, the host lifecycle): for each of its knowledge needs, the
/// loaded knowledge pack that meets it, or why none does.
/// </summary>
[Experimental("AMEXT001")]
public sealed record MindKnowledgeBinding
{
    /// <summary>For each need's index in <see cref="MindDefinition.KnowledgeNeeds"/>, the pack chosen to meet it.</summary>
    public IReadOnlyDictionary<int, LoadedPack> Selected { get; init; } = new Dictionary<int, LoadedPack>().AsReadOnly();
    /// <summary>The needs no loaded pack meets, each with its reason; the Mind is not switched on in full while one is listed.</summary>
    public IReadOnlyList<string> Unmet { get; init; } = [];
    /// <summary>Whether every need is met.</summary>
    public bool IsComplete => Unmet.Count == 0;
}

/// <summary>
/// Matches a Mind's knowledge needs to loaded knowledge packs, as the Mind convention defines (<c>agentmemory.mind/2.0</c> §5): a
/// corpus publishes a signed <c>assets/knowledge-contract.json</c> (<c>agentmemory.knowledge/1</c>: the semantic interface, its
/// edition, languages, jurisdictions, formats and the types that play each role), and a need is met by a pack whose contract
/// names its interface, language, jurisdiction and question format, with at least the minimum live records of each role. A rule
/// of one interface about the fields of its records (a question's options, a source's link) is the host's to add.
/// Data only: it switches nothing on. A host checks it before it switches a Mind on in full, and again whenever the loaded
/// knowledge changes; a need it no longer meets retires the binding (convention §6).
/// </summary>
[Experimental("AMEXT001")]
public static class MindKnowledge
{
    /// <summary>The knowledge contract a corpus publishes.</summary>
    public const string ContractFile = "assets/knowledge-contract.json";
    /// <summary>The knowledge contract's format.</summary>
    public const string ContractFormat = "agentmemory.knowledge/1";

    private const int MaximumContractBytes = 64 << 10;

    private static readonly string[] Fields = ["format", "interface", "edition", "languages", "jurisdictions", "formats", "typeRoles"];

    /// <summary>Binds each of the Mind's knowledge needs to the first pack, in the order given, that meets it.</summary>
    public static MindKnowledgeBinding Bind(MindDefinition mind, IEnumerable<LoadedPack> knowledge, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mind);
        ArgumentNullException.ThrowIfNull(knowledge);
        List<LoadedPack> packs = [.. knowledge];
        var selected = new Dictionary<int, LoadedPack>();
        var unmet = new List<string>();
        for (var index = 0; index < mind.KnowledgeNeeds.Count; index++)
        {
            var need = mind.KnowledgeNeeds[index];
            var reasons = packs.Select(pack => (pack, reason: Unmet(pack, need, now))).ToList();
            if (reasons.FirstOrDefault(r => r.reason is null) is { pack: { } chosen }) selected[index] = chosen;
            else unmet.Add($"{Describe(need)}: " + (packs.Count == 0 ? "no knowledge pack is loaded."
                : string.Join("; ", reasons.Select(r => $"{r.pack.Id} {r.reason}"))));
        }
        return new MindKnowledgeBinding { Selected = selected.AsReadOnly(), Unmet = unmet.AsReadOnly() };
    }

    private static string Describe(JsonElement need) =>
        $"{need.GetProperty("contract").GetString()} in {need.GetProperty("language").GetString()}"
        + (need.TryGetProperty("jurisdiction", out var j) && j.ValueKind == JsonValueKind.String ? $" for {j.GetString()}" : "");

    // Why the pack does not meet the need; null when it does.
    private static string? Unmet(LoadedPack pack, JsonElement need, DateTimeOffset now)
    {
        if (pack.Knowledge is null) return "is not a knowledge pack.";
        if (pack.ValidUntil is { } until && until <= now) return $"was valid until {until:u}.";
        if (!pack.Assets.TryGetValue(ContractFile, out var bytes)) return $"publishes no {ContractFile}.";
        // Signed with the pack, and still read as untrusted data: bounded, and kept to the format's JSON profile first.
        if (bytes.Length > MaximumContractBytes) return $"has a {ContractFile} larger than {MaximumContractBytes >> 10} KiB.";
        if (PackJson.Check(bytes.Span) is { } problem) return $"has a {ContractFile} that {problem}.";
        JsonElement contract;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            contract = document.RootElement.Clone();
        }
        catch (JsonException) { return $"has a malformed {ContractFile}."; }
        if (contract.ValueKind != JsonValueKind.Object || contract.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(Fields.Order(StringComparer.Ordinal)) is false || Text(contract, "format") != ContractFormat)
            return $"has a {ContractFile} that is not '{ContractFormat}' with exactly {string.Join(", ", Fields)}.";
        if (Text(contract, "interface") != need.GetProperty("contract").GetString()) return $"publishes {Text(contract, "interface")}.";
        if (string.IsNullOrWhiteSpace(Text(contract, "edition"))) return "names no edition.";
        if (!Has(contract, "languages", need.GetProperty("language").GetString())) return $"is not in {need.GetProperty("language").GetString()}.";
        if (need.TryGetProperty("jurisdiction", out var jurisdiction) && jurisdiction.ValueKind == JsonValueKind.String && !Has(contract, "jurisdictions", jurisdiction.GetString()))
            return $"is not for {jurisdiction.GetString()}.";
        if (need.TryGetProperty("questionFormat", out var format) && format.ValueKind == JsonValueKind.String && !Has(contract, "formats", format.GetString()))
            return $"has no {format.GetString()} questions.";
        if (contract.GetProperty("typeRoles") is not { ValueKind: JsonValueKind.Object } roleMap
            || roleMap.EnumerateObject().Any(r => r.Value.ValueKind != JsonValueKind.String))
            return "has malformed typeRoles.";
        var roles = roleMap.EnumerateObject().ToDictionary(r => r.Name, r => r.Value.GetString()!, StringComparer.Ordinal);
        var types = pack.Manifest.Ontology?.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (roles.Values.Distinct(StringComparer.Ordinal).Count() != roles.Count || roles.Values.Any(t => !types.Contains(t)))
            return "maps its roles to types its ontology does not define, or two roles to one type.";
        // The records live now: a record past its life, or one a later record of the pack supersedes, does not count.
        var live = pack.Knowledge.Proposals.Where(p => ModuleRecordLife.IsLive(p.Life, now)).ToList();
        var superseded = live.SelectMany(p => p.Supersedes.Select(key => (p.Type, Key: key))).ToHashSet();
        foreach (var minimum in need.GetProperty("minimumRecords").EnumerateObject())
        {
            if (!roles.TryGetValue(minimum.Name, out var type)) return $"plays no '{minimum.Name}' role.";
            var count = live.Count(p => p.Type == type && !superseded.Contains((p.Type, p.Key)));
            if (count < minimum.Value.GetInt32()) return $"has {count} live '{minimum.Name}' records, fewer than {minimum.Value.GetInt32()}.";
        }
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Has(JsonElement contract, string field, string? value) =>
        contract.GetProperty(field) is { ValueKind: JsonValueKind.Array } list && list.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == value);
}
