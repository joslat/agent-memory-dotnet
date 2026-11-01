using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public static partial class PackImports
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
        foreach (var step in mapping.Records.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node is JsonObject o ? o[step] : null;
        if (node is not JsonArray entries) throw new InvalidDataException($"'{(mapping.Records.Length == 0 ? "the top level" : mapping.Records)}' is not an array of entries.");
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal) { mapping.Key, mapping.Text };
        if (mapping.ValidFrom is { } from) taken.Add(from);
        var lines = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        var skipped = new List<string>();
        var index = 0;
        foreach (var entry in entries)
        {
            index++;
            if (entry is not JsonObject fields) { skipped.Add($"entry {index}: not an object"); continue; }
            foreach (var field in fields.Select(f => f.Key).Where(k => !taken.Contains(k))) dropped[field] = dropped.GetValueOrDefault(field) + 1;
            var text = Scalar(fields[mapping.Text]);
            if (string.IsNullOrWhiteSpace(text)) { skipped.Add($"entry {index}: no text in '{mapping.Text}'"); continue; }
            string key;
            if (Scalar(fields[mapping.Key]) is { Length: > 0 } given) key = given;
            else
            {
                if (fields[mapping.Key] is not null) Unusable(dropped, mapping.Key);
                key = KeyOf(text);
            }
            string? at = null;
            if (mapping.ValidFrom is { } f && fields[f] is { } when && (at = Instant(Scalar(when))) is null) Unusable(dropped, f);
            lines.Add((key, text, at is null ? null : new Dictionary<string, string> { ["validFrom"] = at }));
        }
        // R7: the mapping is named in the derivation, so two imports of one export through two mappings differ there too.
        var named = new JsonObject { ["records"] = mapping.Records, ["key"] = mapping.Key, ["text"] = mapping.Text };
        if (mapping.ValidFrom is { } v) named["validFrom"] = v;
        return Write(source, lines, skipped, dropped, named, mapping.Source, id, version, owner, signingKeyPem);
    }

    // A value the mapping names that cannot be used (a key that is not a string or a number, a date that is not ISO 8601):
    // reported with the dropped fields, never lost silently (review 15-17, F8).
    private static void Unusable(SortedDictionary<string, int> dropped, string field) =>
        dropped["unusable '" + field + "'"] = dropped.GetValueOrDefault("unusable '" + field + "'") + 1;

    // A byte order mark (U+FEFF, which some editors write first) is not text (review 15-17, F3).
    private static string Decoded(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');

    /// <summary>Imports an assistant's memory export, one statement a line.</summary>
    public static ImportResult FromText(byte[] source, string sourceName, string id, string version, string owner, string signingKeyPem)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = Decoded(source).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => (Key: KeyOf(l), Text: l, Life: (IReadOnlyDictionary<string, string>?)null)).ToList();
        return Write(source, lines, [], new SortedDictionary<string, int>(), "text", sourceName, id, version, owner, signingKeyPem);
    }

    /// <summary>
    /// Imports a Markdown memory folder (feature 16): each <c>.md</c> file becomes a note keyed by its relative path, its body the
    /// text. Front matter is a strict subset (<c>key: value</c> lines, a string, an integer or a date; anything else refuses
    /// the file); its life fields (<c>validFrom</c>, <c>validTo</c>, <c>expiresAt</c>, <c>reviewBy</c>) are kept and every other
    /// key is reported as dropped. <paramref name="firstCommits"/> gives a file's first commit, the <c>validFrom</c> of a file
    /// that names none. <c>[[wikilinks]]</c> stay in the text; a link to a note the folder does not hold is reported.
    /// </summary>
    /// <remarks>The derivation names the source by a digest over each file's path and digest.</remarks>
    /// <param name="files">Each file's path relative to the folder, with '/' between its parts, and its bytes.</param>
    /// <param name="firstCommits">Each file's first commit, or null without git history.</param>
    /// <param name="id">The pack's id.</param>
    /// <param name="version">The pack's version.</param>
    /// <param name="owner">The owner the card names.</param>
    /// <param name="signingKeyPem">The importer's signing key.</param>
    public static ImportResult FromFolder(IReadOnlyDictionary<string, byte[]> files, IReadOnlyDictionary<string, DateTimeOffset>? firstCommits,
        string id, string version, string owner, string signingKeyPem)
    {
        ArgumentNullException.ThrowIfNull(files);
        var names = files.Keys.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        var known = new HashSet<string>(names.SelectMany(p => new[] { p, p[..^3], Path.GetFileName(p), Path.GetFileNameWithoutExtension(p) })
            .Select(n => n.Normalize(NormalizationForm.FormC)), StringComparer.OrdinalIgnoreCase);
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var lines = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        var source = new List<byte>();
        foreach (var name in names)
        {
            var bytes = files[name];
            // Each file framed: its name's length and name, its digest, and the date git gave it, if any (review 15-17, F11).
            var date = firstCommits?.TryGetValue(name, out var given) == true ? given.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : "";
            foreach (var part in new[] { Encoding.UTF8.GetBytes(name), SHA256.HashData(bytes), Encoding.UTF8.GetBytes(date) })
            {
                source.AddRange(BitConverter.GetBytes(part.Length));
                source.AddRange(part);
            }
            var (front, body, problem) = FrontMatter(Decoded(bytes).Replace("\r\n", "\n"));
            if (problem is not null) { skipped.Add($"'{name}': front matter refused: {problem}"); continue; }
            if (string.IsNullOrWhiteSpace(body)) { skipped.Add($"'{name}': no text"); continue; }
            var life = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in front!)
            {
                if (k is "validFrom" or "validTo" or "expiresAt" or "reviewBy" && Instant(v) is { } at && Regex.IsMatch(v, @"^\d{4}-\d{2}-\d{2}")) life[k] = at;
                else dropped["front matter '" + k + "'"] = dropped.GetValueOrDefault("front matter '" + k + "'") + 1;
            }
            // The first commit dates a note that names no start, unless the note ended before it (review 15-17, F2).
            if (!life.ContainsKey("validFrom") && date.Length != 0 && !(life.TryGetValue("validTo", out var end) && string.CompareOrdinal(end, date) <= 0))
                life["validFrom"] = date;
            foreach (Match link in Regex.Matches(body, @"\[\[([^\[\]|#\n]{1,200})(?:[|#][^\[\]\n]{0,200})?\]\]"))
                if (!known.Contains(link.Groups[1].Value.Trim().Normalize(NormalizationForm.FormC))) skipped.Add($"'{name}': a link to '{link.Groups[1].Value.Trim()}', which the folder does not hold (kept as text)");
            lines.Add((name, body.Trim(), life.Count == 0 ? null : life));
        }
        if (names.Count == 0) throw new InvalidDataException("The folder holds no .md file.");
        return Write([.. source], lines, skipped, dropped, "folder" + (firstCommits is null ? "" : " with git dates"), "a Markdown folder", id, version, owner, signingKeyPem);
    }

    // The strict front matter (R2): '---' lines around 'key: value' lines; a value is a plain or double-quoted string with no
    // escapes, an integer or a date. Indentation, anchors, aliases, tags, flow and block collections, comments, multi-line
    // values and a repeated key are refused, so no YAML parser's surprises reach a pack.
    private static (List<(string Key, string Value)>? Front, string Body, string? Problem) FrontMatter(string text)
    {
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return ([], text, null);
        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        if (end < 0 && text.EndsWith("\n---", StringComparison.Ordinal)) end = text.Length - 4;
        if (end < 0) return (null, "", "no closing '---'");
        var pairs = new List<(string, string)>();
        foreach (var line in end <= 4 ? [] : text[4..end].Split('\n'))
        {
            var m = Regex.Match(line, @"^([A-Za-z][A-Za-z0-9_]{0,63}): (.{1,1000})$");
            if (!m.Success) return (null, "", $"'{Shown(line)}' is not a 'key: value' line");
            var value = m.Groups[2].Value.TrimEnd();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"' && value[1..^1].IndexOfAny(['"', '\\']) < 0) value = value[1..^1];
            else if (value.IndexOfAny(['&', '*', '!', '[', ']', '{', '}', '|', '>', '#', '"', '\'', '`', '%', '@']) >= 0 || value.StartsWith('-') && !Regex.IsMatch(value, @"^-\d+$") || value.Contains(": "))
                return (null, "", $"'{m.Groups[1].Value}' holds YAML beyond a string, an integer or a date");
            if (pairs.Any(p => p.Item1 == m.Groups[1].Value)) return (null, "", $"'{m.Groups[1].Value}' is repeated");
            pairs.Add((m.Groups[1].Value, value));
        }
        return (pairs, text[Math.Min(text.Length, end + 5)..], null);
    }

    private static string Shown(string text) => text.Length <= 60 ? text : text[..60] + "...";

    private static ImportResult Write(byte[] source, List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)> entries, List<string> skipped,
        IReadOnlyDictionary<string, int> dropped, JsonNode mapping, string sourceName, string id, string version, string owner, string signingKeyPem,
        string sourceSignature = "absent", IReadOnlyDictionary<string, string>? extra = null, IReadOnlyList<(string Key, DateTimeOffset At)>? erased = null)
    {
        var kind = mapping is JsonValue ? mapping.GetValue<string>() : "json";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new StringBuilder();
        var written = 0;
        foreach (var (key, text, life) in entries)
        {
            var k = key.Normalize(NormalizationForm.FormC);
            // The loader's own record rules, entry by entry, so an import never writes a pack the loader refuses whole
            // (review 15-17, F1 and F2): a key of 1 to 200 characters, no control character, not blank; a line of at most
            // 64 KiB; a validTo after the validFrom; at most 5,000 records.
            if (k.Length is 0 or > 200 || k.Any(char.IsControl) || string.IsNullOrWhiteSpace(k)) { skipped.Add($"'{Shown(k)}': a key must be 1 to 200 characters, not blank, with no control character"); continue; }
            if (!seen.Add(k)) { skipped.Add($"'{k}': a repeated key, its first entry kept"); continue; }
            if (life is not null && life.TryGetValue("validFrom", out var from) && life.TryGetValue("validTo", out var to) && string.CompareOrdinal(to, from) <= 0)
            { skipped.Add($"'{k}': its validTo is not after its validFrom"); continue; }
            if (written == AgentMemory.Extensibility.Packs.KnowledgePack.MaximumRecords) { skipped.Add($"'{k}': a pack holds at most {AgentMemory.Extensibility.Packs.KnowledgePack.MaximumRecords} records"); continue; }
            var line = new JsonObject { ["type"] = "note", ["key"] = k, ["properties"] = new JsonObject { ["text"] = text.Normalize(NormalizationForm.FormC) } };
            foreach (var (field, at) in life ?? new Dictionary<string, string>()) line[field] = at;
            var json = line.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > AgentMemory.Extensibility.Packs.KnowledgePack.MaximumLineBytes) { skipped.Add($"'{k}': longer than a record holds ({AgentMemory.Extensibility.Packs.KnowledgePack.MaximumLineBytes / 1024} KiB)"); continue; }
            records.Append(json).Append('\n');
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
                ["parameters"] = Parameters(sourceName, kind, mapping, sourceSignature, extra),
            }),
        };
        var archive = PackAuthoring.Build(files, signingKeyPem);
        if (erased is { Count: > 0 }) archive = Erasing(archive, id, erased, signingKeyPem);
        var findings = PackScan.Scan(archive).Findings.Select(f => f.ToString() ?? "").ToList();
        return new ImportResult(archive, written, dropped, skipped, findings);
    }

    // A derivation's parameters are texts: a JSON mapping is named field by field, as mapping.records, mapping.key, ...
    private static JsonObject Parameters(string sourceName, string kind, JsonNode mapping, string sourceSignature, IReadOnlyDictionary<string, string>? extra)
    {
        var parameters = new JsonObject { ["sourceFormat"] = sourceName, ["mapping"] = kind, ["sourceSignature"] = sourceSignature };
        if (mapping is JsonObject fields)
            foreach (var (name, value) in fields) parameters["mapping." + name] = value!.GetValue<string>();
        foreach (var (name, value) in extra ?? new Dictionary<string, string>()) parameters[name] = value;
        return parameters;
    }

    // The source's deletions as this pack's own erasures (feature 15, R3): they reach the records an earlier import of the same
    // source brought, on every host that holds them (feature 08). Signed by the importer, as publisher; the records named by
    // the composed note type the loader gives this pack's notes, read back from the pack itself.
    private static byte[] Erasing(byte[] archive, string id, IReadOnlyList<(string Key, DateTimeOffset At)> erased, string signingKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(signingKeyPem);
        var trust = PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(key.ExportSubjectPublicKeyInfoPem(), id)));
        var type = PackLoader.Load(archive, trust).Knowledge!.Proposals[0].Type;
        var at = erased.Max(e => e.At);
        var now = DateTimeOffset.UtcNow;
        var issued = new DateTimeOffset(Math.Min(at.UtcTicks, now.UtcTicks) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        using var signer = PackSigner.FromPem(signingKeyPem);
        var envelope = PackErasure.Create(id, [.. erased.Select(e => (type, e.Key.Normalize(NormalizationForm.FormC))).Distinct()], "withdrawn", issued, signer);
        var files = PackReader.ReadArchive(archive, "import").Files.ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        files[PackErasure.PathFor(signer.KeyId, issued)] = envelope;
        return PackBuilder.Pack(files);
    }

    // A key for an entry that names none: from its text, so the same statement imports to the same key.
    private static string KeyOf(string text) => "note-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Normalize(NormalizationForm.FormC))))[..16].ToLowerInvariant();

    private static string? Scalar(JsonNode? node) => node is JsonValue value ? value.ToString() : null;

    // An instant the format reads: UTC to the second, from an ISO 8601 date or date-time the source gives, read exactly (a
    // loose parse read 07/08 in US order and "Oct 5" as this year: review 15-17, F8); anything else is null.
    private static readonly string[] IsoForms = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    private static string? Instant(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(text, IsoForms, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            ? t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) : null;
}
