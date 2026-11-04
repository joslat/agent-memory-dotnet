// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;

namespace AgentMemory.Extensibility.Packs;

/// <summary>A projected file: its repository-relative path and its full text.</summary>
[Experimental("AMEXT001")]
public sealed record ProjectionFile(string Path, string Content);

/// <summary>How a projected file on disk stands against what the records give now.</summary>
[Experimental("AMEXT001")]
public enum ProjectionState
{
    /// <summary>Not on disk yet: written.</summary>
    Missing,
    /// <summary>Exactly what the records give.</summary>
    UpToDate,
    /// <summary>Untouched since it was generated, but the records changed: rewritten.</summary>
    Stale,
    /// <summary>Changed by hand since it was generated: never overwritten silently.</summary>
    Edited,
}

/// <summary>One projected file's state, with the records whose blocks differ from what the records give now.</summary>
[Experimental("AMEXT001")]
public sealed record ProjectionCheck(string Path, ProjectionState State, IReadOnlyList<string> Records);

/// <summary>
/// CMP-7: files as projections of the graph. A pack's records render to one deterministic Markdown file: a header naming the
/// module, its version and the digest of everything below it (not the commit: a projection must not change with every commit); then each record as a block
/// marked with its key and revision (a digest of its properties), headed by its title, its type's render line and its
/// properties, sorted by type and key. The same records give the same bytes. A file whose body no longer matches its own
/// digest was edited by hand: the check says so and names the records whose blocks changed, and a write refuses to overwrite
/// it unless forced. An <c>AGENTS.md</c> section per module, between markers, gets the same protection. Edit the sources, not
/// the projection: the projection is what the memory holds, written out.
/// </summary>
[Experimental("AMEXT001")]
public static partial class ModuleProjection
{
    private const string HeaderStart = "<!-- agentmemory:projection ";
    private const string RecordStart = "<!-- agentmemory:record ";

    /// <summary>The projection of one module's snapshot, as one Markdown file under <paramref name="folder"/>.</summary>
    public static ProjectionFile Render(ModuleSnapshot snapshot, string folder = "docs/memory")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var module = snapshot.Module;
        var body = new StringBuilder();
        body.Append("# ").Append(module.Display?.Title ?? module.Id).Append("\n\n");
        // Not the snapshot's source (a commit): the projection would change with every commit, its own included, and a
        // check could never pass. Each record names the file it came from.
        body.Append("Generated from the memory (").Append(module.Id).Append("). Edit the sources it was read from, not this file.\n");
        foreach (var group in snapshot.Records.GroupBy(r => r.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var type = module.Ontology?.Types.FirstOrDefault(t => t.Name == group.Key);
            body.Append("\n## ").Append(Short(group.Key, module.Ontology?.LabelPrefix)).Append(" (").Append(group.Count()).Append(")\n");
            foreach (var record in group.OrderBy(r => r.Key, StringComparer.Ordinal)) body.Append('\n').Append(Block(record, type?.Render));
        }
        var text = body.ToString();
        var header = $"{HeaderStart}module={module.Id} version={module.Version} digest={Digest(text)} -->\n";
        return new ProjectionFile($"{folder.TrimEnd('/')}/{module.Id[(module.Id.LastIndexOf('.') + 1)..]}.md", header + text);
    }

    // One record: marker (key, revision), heading, render line, properties, links.
    private static string Block(ModuleProposal record, string? render)
    {
        var properties = record.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var block = new StringBuilder();
        block.Append(RecordStart).Append("key=\"").Append(Escape(record.Key)).Append("\" revision=").Append(Revision(record)).Append(" -->\n");
        block.Append("### ").Append(Title(record)).Append('\n');
        if (render is not null) block.Append('\n').Append(Rendered(render, record)).Append('\n');
        block.Append('\n');
        foreach (var (name, value) in properties) block.Append("- **").Append(name).Append("**: ").Append(Value(value)).Append('\n');
        foreach (var link in record.Links.OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.Target, StringComparer.Ordinal))
            block.Append("- ").Append(link.Relation).Append(" → ").Append(link.Target).Append('\n');
        return block.ToString();
    }

    /// <summary>A record's revision: the first 16 hex characters of the SHA-256 of its type, key, properties and links, canonical.</summary>
    public static string Revision(ModuleProposal record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var canonical = new StringBuilder().Append(record.Type).Append('\u001F').Append(record.Key);
        foreach (var (name, value) in record.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)) canonical.Append('\u001F').Append(name).Append('=').Append(value.GetRawText());
        foreach (var link in record.Links.OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.Target, StringComparer.Ordinal))
            canonical.Append('\u001F').Append(link.Relation).Append('>').Append(link.Target);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>How each projected file stands on disk under <paramref name="root"/> against what the records give now.</summary>
    public static IReadOnlyList<ProjectionCheck> Check(string root, IEnumerable<ProjectionFile> expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(expected);
        var checks = new List<ProjectionCheck>();
        foreach (var file in expected)
        {
            var full = Path.Combine(root, file.Path);
            if (!File.Exists(full)) { checks.Add(new(file.Path, ProjectionState.Missing, [])); continue; }
            var existing = File.ReadAllText(full).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (existing == file.Content) { checks.Add(new(file.Path, ProjectionState.UpToDate, [])); continue; }
            var changed = ChangedRecords(existing, file.Content);
            checks.Add(new(file.Path, Edited(existing) ? ProjectionState.Edited : ProjectionState.Stale, changed));
        }
        return checks;
    }

    /// <summary>
    /// Writes each projected file under <paramref name="root"/>, except one edited by hand since it was generated (unless
    /// <paramref name="force"/>): a projection never overwrites a person's edit silently. Returns what it found before writing.
    /// </summary>
    public static IReadOnlyList<ProjectionCheck> Write(string root, IEnumerable<ProjectionFile> expected, bool force = false)
    {
        var files = expected.ToList();
        var checks = Check(root, files);
        foreach (var (file, check) in files.Zip(checks))
        {
            if (check.State == ProjectionState.UpToDate || check.State == ProjectionState.Edited && !force) continue;
            var full = Path.Combine(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, file.Content, new UTF8Encoding(false));
        }
        return checks;
    }

    /// <summary>Whether a projected file's body no longer matches the digest its header records: someone edited it.</summary>
    public static bool Edited(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        if (!text.StartsWith(HeaderStart, StringComparison.Ordinal) || end < 0) return true; // no header: not a projection, or its header was edited
        var digest = DigestField().Match(text[..end]);
        return !digest.Success || digest.Groups["d"].Value != Digest(text[(end + 1)..]);
    }

    // The keys whose blocks differ between two projections of one module (added, removed or changed).
    private static IReadOnlyList<string> ChangedRecords(string existing, string expected)
    {
        var before = Blocks(existing);
        var after = Blocks(expected);
        return [.. before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).Order(StringComparer.Ordinal)];
    }

    private static Dictionary<string, string> Blocks(string text)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in BlockPattern().Matches(text)) blocks.TryAdd(match.Groups["k"].Value, match.Value.Trim());
        return blocks;
    }

    /// <summary>
    /// An <c>AGENTS.md</c> with this module's section, between <c>&lt;!-- agentmemory:begin {id} --&gt;</c> and its end marker,
    /// inserted or replaced; everything else as it was. <paramref name="edited"/> is set when the section on disk was changed
    /// by hand since it was generated, and then the content comes back unchanged unless <paramref name="force"/>.
    /// </summary>
    public static string AgentsSection(string? existing, ModuleSnapshot snapshot, ProjectionFile projection, bool force, out bool edited)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(projection);
        var id = snapshot.Module.Id;
        var types = snapshot.Records.GroupBy(r => r.Type).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {Short(g.Key, snapshot.Module.Ontology?.LabelPrefix).ToLowerInvariant()}{(g.Count() == 1 ? "" : "s")}");
        var inner = $"## {snapshot.Module.Display?.Title ?? id} memory\n\n"
            + $"{snapshot.Module.Display?.Description ?? ""}\n\n"
            + $"Held: {string.Join(", ", types)}; read in full in [{projection.Path}]({projection.Path}). Generated from the memory: edit the sources, not this section.\n";
        var section = $"<!-- agentmemory:begin {id} digest={Digest(inner)} -->\n{inner}<!-- agentmemory:end {id} -->\n";
        var text = (existing ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var begin = text.IndexOf($"<!-- agentmemory:begin {id} ", StringComparison.Ordinal);
        var endMarker = $"<!-- agentmemory:end {id} -->\n";
        var end = begin < 0 ? -1 : text.IndexOf(endMarker, begin, StringComparison.Ordinal);
        edited = false;
        if (begin < 0 || end < 0) return text.Length == 0 ? section : text.TrimEnd('\n') + "\n\n" + section;
        var headerEnd = text.IndexOf('\n', begin);
        var current = text[(headerEnd + 1)..end];
        var recorded = DigestField().Match(text[begin..headerEnd]);
        edited = !recorded.Success || recorded.Groups["d"].Value != Digest(current);
        if (edited && !force) return text;
        return text[..begin] + section + text[(end + endMarker.Length)..];
    }

    private static string Title(ModuleProposal record)
    {
        foreach (var name in new[] { "title", "name", "reference", "item" })
            if (record.Properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } title) return OneLine(title);
        return OneLine(record.Key);
    }

    // A type's render template filled from the record: {name} by the property, an absent one left empty.
    private static string Rendered(string render, ModuleProposal record) =>
        OneLine(Placeholder().Replace(render, m => record.Properties.TryGetValue(m.Groups["p"].Value, out var v) ? Value(v) : ""));

    private static string Value(JsonElement value) => value.ValueKind == JsonValueKind.String ? OneLine(value.GetString()!) : value.GetRawText();

    private static string OneLine(string text) => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string Short(string type, string? prefix) => prefix is { Length: > 0 } p && type.StartsWith(p, StringComparison.Ordinal) && type.Length > p.Length ? type[p.Length..] : type;

    private static string Escape(string text) => text.Replace("\"", "'", StringComparison.Ordinal).Replace("--", "- -", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Digest(string text) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [GeneratedRegex(@"digest=(?<d>sha256:[0-9a-f]{64})")]
    private static partial Regex DigestField();

    [GeneratedRegex(@"<!-- agentmemory:record key=""(?<k>[^""]*)""[^\n]*\n(?:(?!<!-- agentmemory:record )[\s\S])*")]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"\{(?<p>[A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}
