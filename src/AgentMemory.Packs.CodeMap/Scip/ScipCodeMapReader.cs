using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Packs.CodeMap.Scip;

/// <summary>
/// Reads a SCIP index into the code map (coding packs CMP-2b): the same records the Roslyn reader writes for C#, for any language
/// a SCIP indexer covers (scip-typescript, scip-python, scip-java, scip-go, rust-analyzer, scip-clang, scip-ruby, scip-dotnet).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it records.</b> Types, and top-level functions (in Python, Go or JavaScript they are most of the code), defined in the
/// index's non-test documents; the documents themselves, in a project per package; and tests. Keys are the SCIP symbols with the
/// version replaced by <c>.</c>, so a key survives a release.
/// </para>
/// <para>
/// <b>Tests.</b> A test file is one the indexer marks as test code or whose path says so (<c>tests/</c>, <c>test_*.py</c>,
/// <c>*_test.go</c>, <c>*.test.ts</c>, <c>*.spec.js</c>, <c>FooTest.java</c>...). Its tests are functions named <c>test...</c> and the
/// methods of a class named <c>...Test</c>, <c>...Tests</c> or <c>...Spec</c>; a test file with none (JavaScript's anonymous
/// <c>it(...)</c> callbacks) is one test. A test links to the recorded types and functions its body refers to, most referred first:
/// the body is the indexer's enclosing range when it gives one, else up to the next test.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public static class ScipCodeMapReader
{
    private static readonly string[] TestFolders = ["test", "tests", "__tests__", "spec", "specs", "testing"];
    private static readonly string[] Fixtures = ["setup", "teardown", "setupclass", "teardownclass", "setup_method", "teardown_method", "beforeeach", "aftereach", "beforeall", "afterall"];

    /// <summary>Reads the SCIP index at <paramref name="path"/> into <paramref name="into"/>, or a new builder.</summary>
    public static CodeMapBuilder Read(string path, CodeMapBuilder? into = null)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, into);
    }

    /// <summary>Reads a SCIP index into <paramref name="into"/> (so one map can hold C# from Roslyn and other languages), or a new builder.</summary>
    public static CodeMapBuilder Read(Stream index, CodeMapBuilder? into = null)
    {
        var scip = ScipIndexReader.Read(index);
        var builder = into ?? new CodeMapBuilder();
        var symbols = new Symbols();
        var documents = scip.Documents.Where(d => d.RelativePath.Length > 0).ToList();
        var information = new Dictionary<string, ScipSymbolInformation>(StringComparer.Ordinal);
        foreach (var symbol in documents.SelectMany(d => d.Symbols)) information.TryAdd(symbol.Symbol, symbol);
        var tests = documents.Where(d => IsTestPath(d.RelativePath) || d.Occurrences.Any(o => (o.Roles & ScipOccurrence.TestRole) != 0))
            .Select(d => d.RelativePath).ToHashSet(StringComparer.Ordinal);

        // Records: the types and top-level functions the non-test documents define.
        var recorded = new Dictionary<string, Recorded>(StringComparer.Ordinal);
        var members = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var document in documents.Where(d => !tests.Contains(d.RelativePath)))
            foreach (var occurrence in document.Occurrences.Where(o => o.IsDefinition))
            {
                if (symbols.Parse(occurrence.Symbol) is not { } symbol) continue;
                information.TryGetValue(occurrence.Symbol, out var info);
                if (Kind(symbol, info) is { } kind) recorded.TryAdd(symbol.Text, new Recorded(symbol, kind, document, occurrence));
                // A member is named on its type: the type's descriptors, then one more.
                if (symbol.Descriptors.Count >= 2 && symbol.Descriptors[^2].Suffix == ScipSuffix.Type
                    && symbol.Descriptors[^1].Suffix is ScipSuffix.Method or ScipSuffix.Term && !symbol.Descriptors[^1].Name.StartsWith('_'))
                    (members.TryGetValue(symbol.Prefix(symbol.Descriptors.Count - 2), out var set) ? set
                        : members[symbol.Prefix(symbol.Descriptors.Count - 2)] = new(StringComparer.Ordinal)).Add(symbol.Descriptors[^1].Name);
            }

        // Projects are packages; a file belongs to the package most of its definitions name.
        var fileProject = new Dictionary<string, string?>(StringComparer.Ordinal);
        var packages = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            var project = document.Occurrences.Where(o => o.IsDefinition).Select(o => symbols.Parse(o.Symbol)).OfType<ScipSymbol>()
                .Where(s => s.Package is { Length: > 0 } and not ".").GroupBy(Project).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key).FirstOrDefault();
            fileProject[document.RelativePath] = project;
            if (project is null) continue;
            if (!packages.TryGetValue(project, out var references)) packages[project] = references = new(StringComparer.Ordinal);
            foreach (var used in document.Occurrences.Where(o => !o.IsDefinition).Select(o => symbols.Owner(o.Symbol, recorded)).OfType<string>())
                if (recorded[used].Symbol is { Package: { Length: > 0 } and not "." } target && Project(target) != project) references.Add(Project(target));
        }
        foreach (var (project, references) in packages)
            builder.AddProject(project, project[(project.IndexOf(':') + 1)..], references.Where(packages.ContainsKey));
        foreach (var document in documents) builder.AddFile(document.RelativePath, fileProject[document.RelativePath], Language(document));

        foreach (var (text, (symbol, kind, document, definition)) in recorded)
        {
            information.TryGetValue(definition.Symbol, out var info);
            builder.AddSymbol(text, info?.DisplayName is { Length: > 0 } display ? display : symbol.Descriptors[^1].Name, FullName(symbol), kind,
                symbol.Package, document.RelativePath, definition.Range.StartLine + 1, members.TryGetValue(text, out var names) ? names : null);
        }

        // What each record uses (CMP-2c): the records its body refers to, when the indexer gives the body's range.
        foreach (var (text, record) in recorded)
        {
            if (record.Definition.Enclosing is not { } body) continue;
            builder.AddUses(text, record.Document.Occurrences
                .Where(o => !o.IsDefinition && o.Range.StartLine >= body.StartLine && o.Range.StartLine <= body.EndLine)
                .Select(o => symbols.Owner(o.Symbol, recorded)).OfType<string>().Where(o => o != text).GroupBy(o => o, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key));
        }

        foreach (var document in documents.Where(d => tests.Contains(d.RelativePath))) AddTests(builder, document, information, recorded, symbols);
        return builder;
    }

    private static void AddTests(CodeMapBuilder builder, ScipDocument document, Dictionary<string, ScipSymbolInformation> information,
        Dictionary<string, Recorded> recorded, Symbols symbols)
    {
        var framework = Language(document);
        var definitions = document.Occurrences.Where(o => o.IsDefinition)
            .Select(o => (Occurrence: o, Symbol: symbols.Parse(o.Symbol))).Where(d => d.Symbol is not null && IsTest(d.Symbol!, information.GetValueOrDefault(d.Occurrence.Symbol)))
            .OrderBy(d => d.Occurrence.Range.StartLine).ToList();
        var references = document.Occurrences.Where(o => !o.IsDefinition).ToList();
        IEnumerable<string> Tested(int first, int last) => references.Where(o => o.Range.StartLine >= first && o.Range.StartLine <= last)
            .Select(o => symbols.Owner(o.Symbol, recorded)).OfType<string>().GroupBy(s => s, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key);

        if (definitions.Count == 0)
        {
            // JavaScript-style tests are calls with a string name, not symbols: the file is the test.
            var stem = Path.GetFileNameWithoutExtension(document.RelativePath);
            builder.AddTest("scip-file " + document.RelativePath, stem, document.RelativePath, 1, framework, Tested(0, int.MaxValue));
            return;
        }
        for (var i = 0; i < definitions.Count; i++)
        {
            var (occurrence, symbol) = definitions[i];
            var first = occurrence.Enclosing?.StartLine ?? occurrence.Range.StartLine;
            var last = occurrence.Enclosing?.EndLine ?? (i + 1 < definitions.Count ? definitions[i + 1].Occurrence.Range.StartLine - 1 : int.MaxValue);
            var owner = symbol!.Descriptors.Count >= 2 && symbol.Descriptors[^2].Suffix == ScipSuffix.Type
                ? symbol.Descriptors[^2].Name : Path.GetFileNameWithoutExtension(document.RelativePath);
            builder.AddTest(symbol.Text, owner + "." + symbol.Descriptors[^1].Name, document.RelativePath, occurrence.Range.StartLine + 1, framework,
                Tested(first, last));
        }
    }

    // A type (last descriptor #), or a function at the top of its namespaces.
    private static CodeMapSymbolKind? Kind(ScipSymbol symbol, ScipSymbolInformation? info)
    {
        var last = symbol.Descriptors[^1];
        if (last.Suffix == ScipSuffix.Type)
            return info?.Kind switch
            {
                7 or 33 or 75 => CodeMapSymbolKind.Class,       // Class, Object, SingletonClass
                49 => CodeMapSymbolKind.Struct,
                21 or 42 or 53 or 56 => CodeMapSymbolKind.Interface, // Interface, Protocol, Trait, TypeClass
                11 => CodeMapSymbolKind.Enum,
                73 => CodeMapSymbolKind.Delegate,
                _ => CodeMapSymbolKind.Type,
            };
        var function = last.Suffix == ScipSuffix.Method || last.Suffix == ScipSuffix.Term && info?.Kind == 17;   // 17: Function
        return function && symbol.Descriptors.Take(symbol.Descriptors.Count - 1).All(d => d.Suffix == ScipSuffix.Namespace)
            ? CodeMapSymbolKind.Function : null;
    }

    private static bool IsTest(ScipSymbol symbol, ScipSymbolInformation? info)
    {
        var last = symbol.Descriptors[^1];
        var function = last.Suffix == ScipSuffix.Method || last.Suffix == ScipSuffix.Term && info?.Kind == 17;
        if (!function || Fixtures.Contains(last.Name.ToLowerInvariant())) return false;
        if (last.Name.StartsWith("test", StringComparison.OrdinalIgnoreCase)) return true;
        return symbol.Descriptors.Count >= 2 && symbol.Descriptors[^2] is { Suffix: ScipSuffix.Type } owner
            && (owner.Name.EndsWith("Test", StringComparison.Ordinal) || owner.Name.EndsWith("Tests", StringComparison.Ordinal)
                || owner.Name.EndsWith("Spec", StringComparison.Ordinal));
    }

    /// <summary>A type or top-level function the map records, and where it is defined.</summary>
    private sealed record Recorded(ScipSymbol Symbol, CodeMapSymbolKind Kind, ScipDocument Document, ScipOccurrence Definition);

    /// <summary>An index names the same symbols over and over: each is parsed once, and its owner looked up once.</summary>
    private sealed class Symbols
    {
        private readonly Dictionary<string, ScipSymbol?> _parsed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _owners = new(StringComparer.Ordinal);

        public ScipSymbol? Parse(string symbol) => _parsed.TryGetValue(symbol, out var known) ? known : _parsed[symbol] = ScipSymbol.Parse(symbol);

        // The recorded type or function a referenced symbol belongs to: itself, or the nearest of its prefixes (a method's type).
        // Asked only once every record is known.
        public string? Owner(string reference, Dictionary<string, Recorded> recorded)
        {
            if (_owners.TryGetValue(reference, out var known)) return known;
            string? owner = null;
            if (Parse(reference) is { } symbol)
                for (var i = symbol.Descriptors.Count - 1; i >= 0 && owner is null; i--)
                    if (recorded.ContainsKey(symbol.Prefix(i))) owner = symbol.Prefix(i);
            return _owners[reference] = owner;
        }
    }

    private static string Project(ScipSymbol symbol) => symbol.Manager + ":" + symbol.Package;

    // Namespaces joined by '/', then the type and its members by '.': `pkg.mod`/Store# reads pkg.mod/Store.
    private static string FullName(ScipSymbol symbol)
    {
        var spaces = symbol.Descriptors.TakeWhile(d => d.Suffix == ScipSuffix.Namespace).Select(d => d.Name).ToList();
        var rest = symbol.Descriptors.Skip(spaces.Count).Where(d => d.Suffix is not (ScipSuffix.Parameter or ScipSuffix.TypeParameter)).Select(d => d.Name);
        var tail = string.Join('.', rest);
        return spaces.Count == 0 ? tail : string.Join('/', spaces) + "/" + tail;
    }

    private static bool IsTestPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (parts[..^1].Any(p => TestFolders.Contains(p, StringComparer.OrdinalIgnoreCase))) return true;
        var file = parts[^1];
        var stem = Path.GetFileNameWithoutExtension(file);
        return file.StartsWith("test_", StringComparison.OrdinalIgnoreCase) || stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase)
            || file.Contains(".test.", StringComparison.OrdinalIgnoreCase) || file.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith("Test", StringComparison.Ordinal) || stem.EndsWith("Tests", StringComparison.Ordinal) || stem.EndsWith("Spec", StringComparison.Ordinal);
    }

    private static string Language(ScipDocument document)
    {
        if (document.Language is { Length: > 0 } language) return language.Length <= 32 ? language.ToLowerInvariant() : language[..32].ToLowerInvariant();
        return Path.GetExtension(document.RelativePath).ToLowerInvariant() switch
        {
            ".py" => "python", ".ts" or ".tsx" or ".mts" or ".cts" => "typescript", ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".java" => "java", ".kt" or ".kts" => "kotlin", ".scala" => "scala", ".go" => "go", ".rs" => "rust", ".rb" => "ruby",
            ".cs" => "csharp", ".c" or ".h" => "c", ".cc" or ".cpp" or ".cxx" or ".hpp" => "cpp", ".php" => "php", ".dart" => "dart",
            _ => "unknown",
        };
    }
}
