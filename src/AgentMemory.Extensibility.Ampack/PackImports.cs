using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// How a JSON export's entries become records (feature 15): <c>agentmemory.import.mapping/1</c>, data rather than code, so a new
/// source needs a mapping file. <see cref="Records"/> is the dotted path to the array of entries (empty for a top-level array);
/// <see cref="Key"/>, <see cref="Text"/> and <see cref="ValidFrom"/> name each entry's fields.
/// </summary>
[Experimental("AMEXT001")]
public sealed record ImportMapping(string Source, string Records, string Key, string Text, string? ValidFrom)
{
    /// <summary>The format a mapping file names.</summary>
    public const string Format = "agentmemory.import.mapping/1";

    /// <summary>Reads a mapping file strictly.</summary>
    /// <exception cref="InvalidDataException">It is not a mapping.</exception>
    public static ImportMapping Read(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("format" or "source" or "records" or "key" or "text" or "validFrom"))
            || Str("format") != Format || Str("source") is not { Length: > 0 } source || Str("records") is not { } records
            || Str("key") is not { Length: > 0 } key || Str("text") is not { Length: > 0 } text
            || root.TryGetProperty("validFrom", out _) && Str("validFrom") is not { Length: > 0 })
            throw new InvalidDataException($"A mapping holds format ('{Format}'), source, records, key, text and optionally validFrom, each a string.");
        return new ImportMapping(source, records, key, text, Str("validFrom"));
    }
}

/// <summary>What an import wrote and what it left out (feature 15, R4: lossless or it says what it dropped).</summary>
/// <param name="Archive">The signed knowledge pack.</param>
/// <param name="Records">The records written.</param>
/// <param name="Dropped">Each source field no mapping took, with how many entries had it.</param>
/// <param name="Skipped">Each entry left out, with why (no text, a repeated key).</param>
/// <param name="Findings">What lint's safety scan found in the pack (feature 04): imported text is untrusted.</param>
[Experimental("AMEXT001")]
public sealed record ImportResult(byte[] Archive, int Records, IReadOnlyDictionary<string, int> Dropped, IReadOnlyList<string> Skipped, IReadOnlyList<string> Findings);

/// <summary>
/// Imports (feature 15): another tool's memory export becomes a knowledge pack of built-in <c>note</c> records, with a derivation
/// naming the source by its digest (<c>actions: import</c>) and a card saying where it came from, signed by whoever imports
/// it. Its source's own trust is never ampack's: the pack is off until its trial passes. Text is normalised to NFC.
/// </summary>
[Experimental("AMEXT001")]
public static class PackImports
{
    /// <summary>The tool a derivation names.</summary>
    public const string Tool = "ampack-import";

    /// <summary>Imports a JSON export through a mapping.</summary>
    /// <exception cref="InvalidDataException">The source is not JSON, or the mapping's path does not lead to an array of objects.</exception>
    public static ImportResult FromJson(byte[] source, ImportMapping mapping, string id, string version, string owner, string signingKeyPem)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mapping);
        JsonNode? root;
        try { root = JsonNode.Parse(source); }
        catch (JsonException e) { throw new InvalidDataException("The source is not JSON: " + e.Message); }
        var node = root;
        foreach (var step in mapping.Records.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node?[step];
        if (node is not JsonArray entries) throw new InvalidDataException($"'{(mapping.Records.Length == 0 ? "the top level" : mapping.Records)}' is not an array of entries.");
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal) { mapping.Key, mapping.Text };
        if (mapping.ValidFrom is { } from) taken.Add(from);
        var lines = new List<(string Key, string Text, string? ValidFrom)>();
        var skipped = new List<string>();
        var index = 0;
        foreach (var entry in entries)
        {
            index++;
            if (entry is not JsonObject fields) { skipped.Add($"entry {index}: not an object"); continue; }
            foreach (var field in fields.Select(f => f.Key).Where(k => !taken.Contains(k))) dropped[field] = dropped.GetValueOrDefault(field) + 1;
            var text = Scalar(fields[mapping.Text]);
            if (string.IsNullOrWhiteSpace(text)) { skipped.Add($"entry {index}: no text in '{mapping.Text}'"); continue; }
            var key = Scalar(fields[mapping.Key]) is { Length: > 0 } given ? given : KeyOf(text);
            lines.Add((key, text, mapping.ValidFrom is { } f ? Instant(Scalar(fields[f])) : null));
        }
        return Write(source, lines, skipped, dropped, "json", mapping.Source, id, version, owner, signingKeyPem);
    }

    /// <summary>Imports an assistant's memory export, one statement a line.</summary>
    public static ImportResult FromText(byte[] source, string sourceName, string id, string version, string owner, string signingKeyPem)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = Encoding.UTF8.GetString(source).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => (Key: KeyOf(l), Text: l, ValidFrom: (string?)null)).ToList();
        return Write(source, lines, [], new SortedDictionary<string, int>(), "text", sourceName, id, version, owner, signingKeyPem);
    }

    private static ImportResult Write(byte[] source, List<(string Key, string Text, string? ValidFrom)> entries, List<string> skipped,
        IReadOnlyDictionary<string, int> dropped, string kind, string sourceName, string id, string version, string owner, string signingKeyPem)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new StringBuilder();
        var written = 0;
        foreach (var (key, text, validFrom) in entries)
        {
            var k = key.Normalize(NormalizationForm.FormC);
            if (!seen.Add(k)) { skipped.Add($"'{k}': a repeated key, its first entry kept"); continue; }
            var line = new JsonObject { ["type"] = "note", ["key"] = k, ["properties"] = new JsonObject { ["text"] = text.Normalize(NormalizationForm.FormC) } };
            if (validFrom is not null) line["validFrom"] = validFrom;
            records.Append(line.ToJsonString()).Append('\n');
            written++;
        }
        if (written == 0) throw new InvalidDataException("The source holds no entry with text to import.");
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        var manifest = new JsonObject
        {
            ["format"] = "agentmemory.module/v1alpha1", ["id"] = id, ["version"] = version, ["sdk"] = "[0.1,0.2)",
            ["seed"] = new JsonArray("records/notes.jsonl"),
        };
        var files = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()),
            ["records/notes.jsonl"] = Encoding.UTF8.GetBytes(records.ToString()),
        }), StringComparer.Ordinal)
        {
            ["card.json"] = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["format"] = "agentmemory.pack.card/1", ["owner"] = new JsonObject { ["name"] = owner },
                ["intendedUse"] = $"Memory imported from a {sourceName} export, as notes.",
                ["limitations"] = "Imported unreviewed: its text is the source's, and nothing here checked it. Trial it before use.",
                ["provenance"] = $"Imported from {sourceName} ({kind}) by {Tool}; the source is named by its digest in META/derivation.json.",
            }.ToJsonString() + "\n"),
            [PackDerivation.FileName] = Jcs.Canonical(new JsonObject
            {
                ["format"] = PackRegistry.DerivationFormat, ["actions"] = new JsonArray("import"),
                ["inputs"] = new JsonArray(new JsonObject { ["digest"] = digest, ["name"] = "source" }),
                ["tool"] = new JsonObject { ["name"] = Tool, ["version"] = "1.0.0" },
                ["parameters"] = new JsonObject { ["sourceFormat"] = sourceName, ["mapping"] = kind, ["sourceSignature"] = "absent" },
            }),
        };
        var archive = PackAuthoring.Build(files, signingKeyPem);
        var findings = PackScan.Scan(archive).Findings.Select(f => f.ToString() ?? "").ToList();
        return new ImportResult(archive, written, dropped, skipped, findings);
    }

    // A key for an entry that names none: from its text, so the same statement imports to the same key.
    private static string KeyOf(string text) => "note-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC))))[..16].ToLowerInvariant();

    private static string? Scalar(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

    // An instant the format reads: UTC to the second, from an ISO date-time the source gives; anything else is left out.
    private static string? Instant(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : null;
}
