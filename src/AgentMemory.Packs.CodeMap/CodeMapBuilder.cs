using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Packs.CodeMap;

/// <summary>The kinds of type a code map records.</summary>
[Experimental("AMEXT001")]
public enum CodeMapSymbolKind
{
    /// <summary>A class.</summary>
    Class,
    /// <summary>A record (class or struct).</summary>
    Record,
    /// <summary>A struct.</summary>
    Struct,
    /// <summary>An interface.</summary>
    Interface,
    /// <summary>An enum.</summary>
    Enum,
    /// <summary>A delegate.</summary>
    Delegate,
    /// <summary>A function at the top of its module (Python, Go, JavaScript), read from a SCIP index.</summary>
    Function,
    /// <summary>Another kind of type: a type alias, a union, or one whose indexer does not say.</summary>
    Type,
}

/// <summary>
/// Collects what a reader finds in a repository and builds the code map's <see cref="ModuleSnapshot"/> (CMP-2a). Paths are
/// repository-relative with forward slashes; symbols are SCIP identities. Links to things the map does not hold are dropped,
/// so a reader can report every reference it sees.
/// </summary>
[Experimental("AMEXT001")]
public sealed class CodeMapBuilder
{
    /// <summary>Longest record key: longer SCIP identities keep a readable head and a hash (the full one is the <c>scip</c> property).</summary>
    public const int MaximumKeyLength = 200;
    /// <summary>Most public member names kept on a symbol, by length of the joined text.</summary>
    public const int MaximumMembersLength = 2000;
    /// <summary>Most symbols one test links to: the ones it touches most.</summary>
    public const int MaximumTestedSymbols = 12;
    /// <summary>Most symbols one symbol links to as using them: the ones its members mention most.</summary>
    public const int MaximumUsedSymbols = 16;

    private readonly SortedDictionary<string, (string Name, SortedSet<string> References)> _projects = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, (string? Project, string Language)> _files = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Symbol> _symbols = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Test> _tests = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, IReadOnlyList<string>> _uses = new(StringComparer.Ordinal);

    private sealed record Symbol(string Name, string FullName, CodeMapSymbolKind Kind, string Project, SortedSet<string> Files, string? File, int Line, string Members);
    private sealed record Test(string Name, string File, int Line, string Framework, IReadOnlyList<string> Tested);

    /// <summary>The number of records added so far, by type.</summary>
    public (int Projects, int Files, int Symbols, int Tests) Counts => (_projects.Count, _files.Count, _symbols.Count, _tests.Count);

    /// <summary>A project at a repository path (its .csproj), with the projects it references.</summary>
    public void AddProject(string path, string name, IEnumerable<string>? references = null)
    {
        var key = Path(path);
        var refs = new SortedSet<string>((references ?? []).Select(Path).Where(r => r != key), StringComparer.Ordinal);
        if (_projects.TryGetValue(key, out var known)) refs.UnionWith(known.References);
        _projects[key] = (name, refs);
    }

    /// <summary>A source file, in a project when it belongs to one.</summary>
    public void AddFile(string path, string? project, string language = "csharp") => _files[Path(path)] = (project is null ? null : Path(project), language);

    /// <summary>A type declared in a file (a partial type in several files is one symbol defined by each).</summary>
    public void AddSymbol(string scip, string name, string fullName, CodeMapSymbolKind kind, string project, string file, int line, IEnumerable<string>? members = null)
    {
        var path = Path(file);
        if (_symbols.TryGetValue(scip, out var known)) { known.Files.Add(path); return; }
        var text = new StringBuilder();
        foreach (var member in (members ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (text.Length + member.Length + 1 > MaximumMembersLength) break;
            if (text.Length > 0) text.Append(' ');
            text.Append(member);
        }
        _symbols[scip] = new Symbol(name, fullName, kind, project, new SortedSet<string>([path], StringComparer.Ordinal), path, line, text.ToString());
    }

    /// <summary>
    /// The symbols a symbol's members use, most used first (CMP-2c): a change to one reaches the tests of the code that uses it.
    /// </summary>
    public void AddUses(string scip, IEnumerable<string> used) =>
        _uses[scip] = [.. used.Distinct(StringComparer.Ordinal).Where(s => s != scip)];

    /// <summary>A test method and the symbols it touches, most touched first.</summary>
    public void AddTest(string scip, string name, string file, int line, string framework, IEnumerable<string> tested) =>
        _tests[scip] = new Test(name, Path(file), line, framework, [.. tested.Distinct(StringComparer.Ordinal).Where(s => s != scip)]);

    /// <summary>The snapshot of everything added, taken from <paramref name="source"/> (such as <c>git:</c> and the commit).</summary>
    public ModuleSnapshot Build(string source)
    {
        var records = new List<ModuleProposal>();
        foreach (var (path, project) in _projects)
            records.Add(Record(CodeMapPack.ProjectType, path, path, 0, new() { ["name"] = project.Name, ["path"] = path },
                project.References.Where(_projects.ContainsKey).Select(r => ("CODEMAP_PROJECT_REFERENCES", r))));
        foreach (var (path, file) in _files)
        {
            var links = new List<(string, string)>();
            if (file.Project is { } p && _projects.ContainsKey(p)) links.Add(("CODEMAP_IN_PROJECT", p));
            links.AddRange(_symbols.Where(s => s.Value.Files.Contains(path)).Select(s => ("CODEMAP_DEFINES", Key(s.Key))));
            records.Add(Record(CodeMapPack.FileType, path, path, 0, new() { ["path"] = path, ["language"] = file.Language }, links));
        }
        foreach (var (scip, symbol) in _symbols)
        {
            var properties = new Dictionary<string, string>
            {
                ["name"] = symbol.Name, ["fullName"] = Clip(symbol.FullName, 512), ["kind"] = symbol.Kind.ToString().ToLowerInvariant(),
                ["project"] = Clip(symbol.Project, 200), ["scip"] = Clip(scip, 600),
            };
            if (symbol.File is { } file) properties["file"] = file;
            if (symbol.Members.Length > 0) properties["members"] = symbol.Members;
            var uses = _uses.TryGetValue(scip, out var used)
                ? used.Where(_symbols.ContainsKey).Take(MaximumUsedSymbols).Select(s => ("CODEMAP_USES", Key(s))) : [];
            records.Add(Record(CodeMapPack.SymbolType, Key(scip), symbol.File ?? "", symbol.Line, properties, uses));
        }
        foreach (var (scip, test) in _tests)
        {
            var links = test.Tested.Where(_symbols.ContainsKey).Take(MaximumTestedSymbols).Select(s => ("CODEMAP_TESTS", Key(s))).ToList();
            if (_files.ContainsKey(test.File)) links.Add(("CODEMAP_DECLARED_IN", test.File));
            records.Add(Record(CodeMapPack.TestType, Key(scip), test.File, test.Line,
                new() { ["name"] = Clip(test.Name, 400), ["file"] = test.File, ["framework"] = test.Framework, ["scip"] = Clip(scip, 600) }, links));
        }
        return new ModuleSnapshot { Module = CodeMapPack.Manifest, Source = source, Records = records.AsReadOnly() };
    }

    /// <summary>A record key for a SCIP identity: itself when short enough, else a readable head and a hash of the whole.</summary>
    public static string Key(string scip) => scip.Length <= MaximumKeyLength ? scip
        : scip[..(MaximumKeyLength - 17)] + "~" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scip)))[..16].ToLowerInvariant();

    private static ModuleProposal Record(string type, string key, string file, int line, Dictionary<string, string> properties,
        IEnumerable<(string Relation, string Target)> links) => new()
    {
        Type = type, Key = key,
        Properties = properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
        Links = links.Distinct().Select(l => new ProposedLink { Relation = l.Relation, Target = l.Target }).ToList().AsReadOnly(),
        Source = new ProposalSource { Excerpt = file, Pack = new PackSource { Id = CodeMapPack.Id, Version = "", Digest = "", File = file, Line = line } },
    };

    private static string Path(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value.TrimStart('/');
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
}
