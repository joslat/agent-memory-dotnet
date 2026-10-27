using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Packs.Decisions;

/// <summary>A repository's files and declared type names, for turning what a decision mentions into what it applies to.</summary>
[Experimental("AMEXT001")]
public sealed partial class CodeIndex
{
    private readonly HashSet<string> _files;
    private readonly Dictionary<string, List<string>> _byName;
    private readonly Dictionary<string, List<string>> _types;

    /// <summary>Builds an index from repository-relative paths and, for each declared type name, the files that declare it.</summary>
    public CodeIndex(IEnumerable<string> files, IReadOnlyDictionary<string, IReadOnlyList<string>> types)
    {
        _files = files.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _byName = _files.GroupBy(f => f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        _types = types.ToDictionary(t => t.Key, t => t.Value.Select(Normalize).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
    }

    /// <summary>Scans the given repository-relative files under <paramref name="root"/>: every path, and the classes, records, structs,
    /// interfaces and enums the .cs files declare (a text scan, enough to recognise names a decision mentions).</summary>
    public static CodeIndex Scan(string root, IReadOnlyList<string> files)
    {
        var types = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(root, file)); }
            catch (IOException) { continue; }
            foreach (Match match in Declaration().Matches(text))
                (types.TryGetValue(match.Groups[1].Value, out var list) ? list : types[match.Groups[1].Value] = []).Add(file);
        }
        return new CodeIndex(files, types.ToDictionary(t => t.Key, t => (IReadOnlyList<string>)t.Value.Distinct(StringComparer.Ordinal).ToList()));
    }

    /// <summary>Whether a repository path is a file the index holds.</summary>
    public bool HasFile(string path) => _files.Contains(Normalize(path));
    /// <summary>Whether any file sits under a folder.</summary>
    public bool HasFolder(string folder) { var prefix = Normalize(folder).TrimEnd('/') + "/"; return _files.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)); }
    /// <summary>The files a bare file name could be: one when it is unique.</summary>
    public IReadOnlyList<string> FilesNamed(string name) => _byName.TryGetValue(name, out var files) ? files : [];
    /// <summary>The files that declare a type name; empty when it is not a type of this repository.</summary>
    public IReadOnlyList<string> TypeFiles(string name) => _types.TryGetValue(name, out var files) ? files : [];

    internal static string Normalize(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value.TrimStart('/');
    }

    [GeneratedRegex(@"\b(?:class|record|struct|interface|enum)\s+([A-Z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Declaration();
}

/// <summary>
/// Reads decisions from a project's Markdown (coding packs CMP-3) into the decisions pack's snapshot, deterministically: ADR and
/// MADR files (one decision each, sections Status, Context, Decision, Consequences) and Squad's decision log (dated, D-numbered
/// or directive entries; entries that only review something are skipped). What a decision applies to comes from the paths,
/// file names and type names its text mentions that the code index holds.
/// </summary>
[Experimental("AMEXT001")]
public static partial class DecisionsReader
{
    private const int MaximumText = 2000;

    /// <summary>The usual places: <c>.squad/decisions.md</c> and Markdown files under <c>docs/adr</c>, <c>docs/decisions</c>, <c>adr</c> or <c>decisions</c>.</summary>
    public static IReadOnlyList<string> DecisionFiles(IEnumerable<string> repositoryFiles) =>
    [
        .. repositoryFiles.Select(CodeIndex.Normalize).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && (f.Equals(".squad/decisions.md", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(f, @"^(?:docs/)?(?:adr|adrs|decisions|architecture/decisions)/[^/]+\.md$", RegexOptions.IgnoreCase))
            && !f.EndsWith("README.md", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("template.md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The decisions in the given Markdown files under <paramref name="root"/>, as a snapshot taken from <paramref name="source"/>.</summary>
    public static ModuleSnapshot Read(string root, IReadOnlyList<string> markdownFiles, CodeIndex code, string source)
    {
        var records = new List<(Decision Decision, ModuleProposal Record)>();
        foreach (var file in markdownFiles)
        {
            var text = File.ReadAllText(Path.Combine(root, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var found = file.EndsWith(".squad/decisions.md", StringComparison.OrdinalIgnoreCase) || file.Equals(".squad/decisions.md", StringComparison.OrdinalIgnoreCase)
                ? SquadEntries(text, file) : [Adr(text, file)];
            foreach (var decision in found.OfType<Decision>())
                records.Add((decision, Record(decision, code)));
        }
        // Keys repeat when two entries share a heading: keep the first, as Markdown readers do.
        var unique = records.GroupBy(r => r.Record.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        return new ModuleSnapshot { Module = DecisionsPack.Manifest, Source = source, Records = Supersessions(unique) };
    }

    /// <summary>
    /// ADR supersession as links (CP-acceptance): "Supersedes ADR-2", or a link to the older file, in the newer decision, or
    /// "Superseded by …" in the older one, becomes a <c>DECISIONS_SUPERSEDES</c> link from the newer to the older; the older then
    /// reads as superseded and applies to nothing, whatever its own file still says. A reference to nothing this read holds is
    /// left alone.
    /// </summary>
    internal static IReadOnlyList<ModuleProposal> Supersessions(IReadOnlyList<(Decision Decision, ModuleProposal Record)> found)
    {
        var byFile = found.Where(f => f.Decision.Anchor.Length == 0).GroupBy(f => f.Decision.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Record.Key, StringComparer.OrdinalIgnoreCase);
        var byNumber = new Dictionary<int, string>();
        foreach (var (file, key) in byFile.OrderBy(f => f.Key, StringComparer.Ordinal))
            if (AdrFile().Match(Path.GetFileName(file)) is { Success: true } m && int.TryParse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture, out var n))
                byNumber.TryAdd(n, key);
        string? Resolve(string from, string reference)
        {
            if (MarkdownTarget().Match(reference) is { Success: true } link)
            {
                var folder = Path.GetDirectoryName(from)?.Replace('\\', '/') ?? "";
                var parts = new List<string>();
                foreach (var part in CodeIndex.Normalize(folder.Length == 0 ? link.Groups["path"].Value : folder + "/" + link.Groups["path"].Value).Split('/'))
                    if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                    else if (part is not ("." or "")) parts.Add(part);
                if (byFile.TryGetValue(string.Join('/', parts), out var linked)) return linked;
            }
            return AdrReference().Match(reference) is { Success: true } number
                && int.TryParse(number.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture, out var n) && byNumber.TryGetValue(n, out var numbered) ? numbered : null;
        }
        var supersedes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (decision, record) in found.Where(f => f.Decision.Anchor.Length == 0))
            foreach (var line in decision.Body.Split('\n'))
            {
                if (SupersedesLine().Match(line) is { Success: true } newer && Resolve(decision.File, newer.Groups["rest"].Value) is { } older && older != record.Key)
                    supersedes.TryAdd(record.Key, older);
                if (SupersededByLine().Match(line) is { Success: true } by && Resolve(decision.File, by.Groups["rest"].Value) is { } successor && successor != record.Key)
                    supersedes.TryAdd(successor, record.Key);
            }
        var superseded = supersedes.Values.ToHashSet(StringComparer.Ordinal);
        return [.. found.Select(f =>
        {
            var record = f.Record;
            if (supersedes.TryGetValue(record.Key, out var target))
                record = record with { Links = [.. record.Links, new ProposedLink { Relation = DecisionsPack.Supersedes, Target = target }] };
            if (superseded.Contains(record.Key))
            {
                var properties = record.Properties.Where(p => p.Key is not ("appliesTo" or "mentions")).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                properties["status"] = JsonSerializer.SerializeToElement("superseded");
                record = record with { Properties = properties.AsReadOnly() };
            }
            return record;
        })];
    }

    [GeneratedRegex(@"^(?:adr-?)?(?<n>\d{1,4})[-_]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AdrFile();

    [GeneratedRegex(@"\]\((?<path>[^)#\s]+\.md)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownTarget();

    [GeneratedRegex(@"(?:\b(?:ADR|MADR)[-\s#]*|#)0*(?<n>\d{1,4})\b|\b0*(?<n>\d{1,4})-[a-z]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AdrReference();

    [GeneratedRegex(@"\bsupersedes\b(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SupersedesLine();

    [GeneratedRegex(@"\bsuperseded\s+by\b(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SupersededByLine();

    internal sealed record Decision(string File, int Line, string Anchor, string Title, string Status, string? Date, string? By,
        string? Context, string DecisionText, string? Consequences, string Body);

    // One ADR or MADR file: its first heading is the title; sections by heading name.
    internal static Decision? Adr(string text, string file)
    {
        var lines = text.Split('\n');
        var title = lines.Select((l, i) => (l, i)).FirstOrDefault(p => p.l.StartsWith("# ", StringComparison.Ordinal));
        if (title.l is null) return null;
        var sections = Sections(lines, title.i + 1, lines.Length, level: 2);
        var status = Section(sections, "status") ?? Field(text, "status");
        var decision = Section(sections, "decision") ?? Section(sections, "decision outcome");
        if (decision is null) return null;
        return new Decision(file, title.i + 1, "", Clean(AdrNumber().Replace(title.l[2..], "")), Status(status, text), Field(text, "date"),
            Field(text, "deciders") ?? Field(text, "decision-makers"), Section(sections, "context") ?? Section(sections, "context and problem statement"),
            decision, Section(sections, "consequences"), text);
    }

    // Squad's log: an entry is a heading that is dated, D-numbered or a directive, or whose block has a Decision section.
    internal static IEnumerable<Decision?> SquadEntries(string text, string file)
    {
        var lines = text.Split('\n');
        var headings = lines.Select((l, i) => (Level: Level(l), Text: l.TrimStart('#').Trim(), Line: i)).Where(h => h.Level is >= 1 and <= 3).ToList();
        for (var h = 0; h < headings.Count; h++)
        {
            var (level, heading, line) = headings[h];
            var end = headings.Skip(h + 1).FirstOrDefault(n => n.Level <= level) is { Text: not null } next && next.Line > line ? next.Line : lines.Length;
            var dated = DatedHeading().Match(heading);
            var numbered = NumberedHeading().Match(heading);
            var sections = Sections(lines, line + 1, end, level + 1);
            var decisionSection = Section(sections, "decision") ?? Sections(lines, line + 1, end, level + 2).Where(s => s.Key.StartsWith("decision", StringComparison.Ordinal))
                .Select(s => s.Value).FirstOrDefault();
            var directive = heading.Contains("directive", StringComparison.OrdinalIgnoreCase);
            if (!dated.Success && !numbered.Success && decisionSection is null && !directive) continue;
            if (heading.Contains("review", StringComparison.OrdinalIgnoreCase) && decisionSection is null && !numbered.Success) continue;
            var body = string.Join('\n', lines[(line + 1)..end]).Trim();
            if (body.Length == 0) continue;
            var title = numbered.Success ? numbered.Groups["title"].Value : dated.Success ? dated.Groups["title"].Value : heading;
            var by = numbered.Success && numbered.Groups["by"].Success ? numbered.Groups["by"].Value : Field(body, "by");
            var date = numbered.Success && numbered.Groups["date"].Success ? numbered.Groups["date"].Value : dated.Success ? dated.Groups["date"].Value : Field(body, "date");
            yield return new Decision(file, line + 1, Slug(heading), Clean(title), Status(Field(body, "status"), body), date, by,
                Section(sections, "context"), decisionSection ?? FirstParagraphs(body), Section(sections, "rationale") ?? Section(sections, "consequences"), body);
        }
    }

    private static ModuleProposal Record(Decision decision, CodeIndex code)
    {
        var (applies, mentions) = Targets(decision.Body, code);
        var superseded = decision.Status == "superseded";
        var properties = new Dictionary<string, string?>
        {
            ["title"] = Clip(decision.Title, 300), ["status"] = decision.Status, ["date"] = decision.Date is { } d ? Clip(d, 40) : null,
            ["context"] = Clip(decision.Context, MaximumText), ["decision"] = Clip(decision.DecisionText, MaximumText) ?? decision.Title,
            ["consequences"] = Clip(decision.Consequences, MaximumText), ["by"] = decision.By is { } b ? Clip(b, 200) : null,
            ["source"] = Clip(decision.File + (decision.Anchor.Length > 0 ? "#" + decision.Anchor : ""), 512),
            // A superseded decision applies to nothing: only the decision that replaced it should come up with the code.
            ["appliesTo"] = superseded || applies.Count == 0 ? null : Clip(string.Join(' ', applies), 4000),
            ["mentions"] = superseded || mentions.Count == 0 ? null : Clip(string.Join(", ", mentions), 2000),
        };
        return new ModuleProposal
        {
            Type = DecisionsPack.Type, Key = Clip(decision.File + "#" + (decision.Anchor.Length > 0 ? decision.Anchor : "decision"), 200)!,
            Properties = properties.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
            Source = new ProposalSource { Excerpt = decision.File, Pack = new PackSource { Id = DecisionsPack.Id, Version = "", Digest = "", File = decision.File, Line = decision.Line } },
        };
    }

    /// <summary>The paths and types a text mentions that the code index holds: files exactly, folders as <c>folder/**</c>, types by their files.</summary>
    internal static (IReadOnlyList<string> AppliesTo, IReadOnlyList<string> Mentions) Targets(string text, CodeIndex code)
    {
        var applies = new SortedSet<string>(StringComparer.Ordinal);
        var mentions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in PathToken().Matches(text))
        {
            var token = CodeIndex.Normalize(match.Value.Trim('`', '*', '(', ')', '.', ',', ':', ';', '"', '\''));
            if (token.Length < 3) continue;
            if (code.HasFile(token)) applies.Add(token);
            else if (!token.Contains('/') && code.FilesNamed(token) is { Count: 1 } single) applies.Add(single[0]);
            else if (token.Contains('/') && code.HasFolder(token)) applies.Add(token.TrimEnd('/') + "/**");
        }
        foreach (Match match in TypeToken().Matches(text))
        {
            var name = match.Value;
            var files = code.TypeFiles(name);
            if (files.Count is 0 or > 3) continue;   // not a type here, or too common a name to say anything
            mentions.Add(name);
            applies.UnionWith(files);
        }
        return (applies.Take(40).ToList(), mentions.Take(40).ToList());
    }

    private static Dictionary<string, string> Sections(string[] lines, int start, int end, int level)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null; var buffer = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var l = Level(lines[i]);
            if (l == level)
            {
                if (name is not null && !sections.ContainsKey(name)) sections[name] = buffer.ToString().Trim();
                name = Regex.Replace(lines[i].TrimStart('#').Trim().ToLowerInvariant(), @"[:*]", "").Trim(); buffer.Clear();
            }
            else if (l is > 0 && l < level) break;
            else if (name is not null) buffer.AppendLine(lines[i]);
        }
        if (name is not null && !sections.ContainsKey(name)) sections[name] = buffer.ToString().Trim();
        return sections;
    }

    private static string? Section(Dictionary<string, string> sections, string name) =>
        sections.FirstOrDefault(s => s.Key == name || s.Key.StartsWith(name + " ", StringComparison.Ordinal) || s.Key.StartsWith(name + ":", StringComparison.Ordinal))
            .Value is { Length: > 0 } value ? value : null;

    // "Status: accepted", "**Status:** Accepted", "* Status: proposed".
    private static string? Field(string text, string name) =>
        Regex.Match(text, @"(?im)^[\s>*-]*\**" + Regex.Escape(name) + @"\**\s*:\**\s*(.+)$") is { Success: true } m ? m.Groups[1].Value.Trim().Trim('*').Trim() : null;

    private static string Status(string? declared, string body)
    {
        var value = (declared ?? "").ToLowerInvariant();
        if (value.Contains("supersed", StringComparison.Ordinal) || Regex.IsMatch(body, @"(?i)\bsuperseded by\b")) return "superseded";
        if (value.Contains("deprecat", StringComparison.Ordinal) || value.Contains("reject", StringComparison.Ordinal)) return "deprecated";
        if (value.Contains("propos", StringComparison.Ordinal) || value.Contains("draft", StringComparison.Ordinal)) return "proposed";
        return "accepted";
    }

    private static int Level(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '#') count++;
        return count > 0 && count < line.Length && line[count] == ' ' ? count : 0;
    }

    private static string FirstParagraphs(string body)
    {
        var text = new StringBuilder();
        foreach (var paragraph in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (paragraph.TrimStart().StartsWith('#')) break;
            if (text.Length + paragraph.Length > MaximumText) break;
            text.AppendLine(paragraph.Trim()).AppendLine();
        }
        return text.Length > 0 ? text.ToString().Trim() : body[..Math.Min(body.Length, MaximumText)];
    }

    private static string Slug(string heading) => Regex.Replace(heading.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } slug
        ? slug[..Math.Min(slug.Length, 120)] : "decision";

    private static string Clean(string value) => Regex.Replace(value.Replace("**", "", StringComparison.Ordinal).Trim(), @"\s+", " ");

    private static string? Clip(string? value, int length) => value is null ? null : value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"^(?<date>\d{4}-\d{2}-\d{2}(?:T[\d:.+\-]+)?)\s*[:—-]\s*(?<title>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DatedHeading();

    [GeneratedRegex(@"^(?<id>D-[A-Za-z0-9-]+)\s*:\s*(?<title>.+?)(?:\s*\((?<by>[^,()]+),\s*(?<date>\d{4}-\d{2}-\d{2})\))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"^(?:ADR|MADR)?[-\s]*\d+[:.\s-]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AdrNumber();

    [GeneratedRegex(@"[`'""(]?[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-*]+)+/?|[A-Za-z0-9_\-]+\.(?:cs|csproj|json|md|props|yml|yaml)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PathToken();

    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex TypeToken();
}
