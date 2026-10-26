using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgentMemory.Packs.CodeMap.Roslyn;

/// <summary>
/// Reads a C# repository into the code map (coding packs CMP-2a) with the Roslyn compiler, without MSBuild or a build: one
/// compilation per project (its own files, the projects it references, the runtime's reference assemblies), so in-repo types
/// resolve exactly while external packages simply stay unresolved. Records projects, files, types and test methods, and links
/// each test to the in-repo types it touches most, outside its own project.
/// </summary>
/// <remarks>Deterministic: the same files give the same records. Keys are SCIP symbols (<c>scip-dotnet nuget Assembly . Ns/Type#</c>)
/// with the version slot empty, so they survive releases.</remarks>
[Experimental("AMEXT001")]
public static class RoslynCodeMapReader
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal)
    {
        "Fact", "Theory", "SkippableFact", "SkippableTheory", "AvaloniaFact", "AvaloniaTheory", "RetryFact",
        "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
    };

    private static readonly Lazy<IReadOnlyList<MetadataReference>> Runtime = new(() =>
        [.. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))]);

    /// <summary>Reads the repository at <paramref name="root"/>: the files Git tracks there, or every file outside bin/obj when it is not a Git repository.</summary>
    public static CodeMapBuilder Read(string root, CancellationToken cancellationToken = default) =>
        Read(root, TrackedFiles(root), cancellationToken);

    /// <summary>Reads the given repository-relative files (.csproj and .cs) under <paramref name="root"/>.</summary>
    public static CodeMapBuilder Read(string root, IReadOnlyList<string> files, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root); ArgumentNullException.ThrowIfNull(files);
        var builder = new CodeMapBuilder();
        var paths = files.Select(Normalize).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var projects = paths.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(p => Project.Load(root, p)).ToDictionary(p => p.Path, StringComparer.Ordinal);
        var sources = paths.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var project in projects.Values) builder.AddProject(project.Path, project.Name, project.References);
        // Each file belongs to the deepest project folder that contains it.
        var byProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in sources)
        {
            var owner = projects.Values.Where(p => p.Folder.Length == 0 || file.StartsWith(p.Folder + "/", StringComparison.Ordinal))
                .MaxBy(p => p.Folder.Length);
            builder.AddFile(file, owner?.Path);
            if (owner is not null) (byProject.TryGetValue(owner.Path, out var list) ? list : byProject[owner.Path] = []).Add(file);
        }

        // Pass 1: compile in reference order and declare every type.
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        // Keyed by assembly and documentation id: a type seen through a project reference is not always the same symbol object.
        var declared = new Dictionary<string, (string Scip, string Project)>(StringComparer.Ordinal);
        foreach (var project in Order(projects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trees = (byProject.TryGetValue(project.Path, out var mine) ? mine : [])
                .Select(f => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(Path.Combine(root, f))),
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), f, cancellationToken)).ToList();
            var references = Runtime.Value.Concat(Closure(project, projects).Where(compilations.ContainsKey).Select(r => (MetadataReference)compilations[r].ToMetadataReference()));
            var compilation = CSharpCompilation.Create(project.Name, trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
            compilations[project.Path] = compilation;
            foreach (var tree in trees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes().Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
                {
                    if (model.GetDeclaredSymbol(node, cancellationToken) is not INamedTypeSymbol type) continue;
                    var scip = Scip(project.Name, type);
                    declared[Identity(type)] = (scip, project.Path);
                    builder.AddSymbol(scip, type.Name, type.ToDisplayString(), Kind(type), project.Name, tree.FilePath,
                        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        type.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared && m.CanBeReferencedByName)
                            .Select(m => m.Name));
                }
            }
        }

        // Pass 2: tests, with the in-repo types they touch outside their own project, the method's own first.
        foreach (var (path, compilation) in compilations)
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(tree);
                foreach (var method in tree.GetRoot(cancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    var attribute = method.AttributeLists.SelectMany(l => l.Attributes).Select(a => AttributeName(a.Name)).FirstOrDefault(TestAttributes.Contains);
                    if (attribute is null || model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol) continue;
                    // What the method itself touches, most first; only a test that touches nothing itself (it delegates to helpers)
                    // falls back to its class's: class-wide types on every test would bury the ones that matter.
                    var touched = Touched(model, method, declared, path, cancellationToken).ToList();
                    if (touched.Count == 0 && method.Parent is TypeDeclarationSyntax owner) touched = [.. Touched(model, owner, declared, path, cancellationToken)];
                    var tested = touched.GroupBy(s => s.Scip).Select(g => (g.Key, Weight: g.Sum(x => x.Weight)))
                        .OrderByDescending(g => g.Weight).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key);
                    builder.AddTest(Scip(compilation.AssemblyName!, symbol.ContainingType) + symbol.Name + "().",
                        symbol.ContainingType.Name + "." + symbol.Name, tree.FilePath, method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Framework(attribute), tested);
                }
            }
        return builder;
    }

    // The in-repo types a node's names bind to, outside the test's own project, one weight per mention.
    private static IEnumerable<(string Scip, int Weight)> Touched(SemanticModel model, SyntaxNode node,
        IReadOnlyDictionary<string, (string Scip, string Project)> declared, string project, CancellationToken cancellationToken)
    {
        const int weight = 1;
        foreach (var name in node.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(name, cancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            var type = symbol switch
            {
                INamedTypeSymbol named => named,
                IMethodSymbol { MethodKind: MethodKind.Constructor } ctor => ctor.ContainingType,
                ILocalSymbol local => local.Type as INamedTypeSymbol,
                IParameterSymbol parameter => parameter.Type as INamedTypeSymbol,
                not null => symbol.ContainingType,
                _ => null,
            };
            if (type is not null && declared.TryGetValue(Identity(type), out var found) && found.Project != project)
                yield return (found.Scip, weight);
        }
    }

    private static string Identity(INamedTypeSymbol type) =>
        type.OriginalDefinition.ContainingAssembly?.Name + "|" + type.OriginalDefinition.GetDocumentationCommentId();

    /// <summary>The SCIP identity of a type: <c>scip-dotnet nuget Assembly . Ns/Inner/Outer#Nested#</c>.</summary>
    internal static string Scip(string assembly, INamedTypeSymbol type)
    {
        var descriptors = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType) descriptors.Insert(0, Escape(t.Name) + "#");
        var ns = new List<string>();
        for (var n = type.ContainingNamespace; n is { IsGlobalNamespace: false }; n = n.ContainingNamespace) ns.Insert(0, Escape(n.Name) + "/");
        return "scip-dotnet nuget " + assembly + " . " + string.Concat(ns) + string.Concat(descriptors);
    }

    private static string Escape(string name) => name.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '-' or '$') ? name : "`" + name.Replace("`", "``") + "`";

    private static CodeMapSymbolKind Kind(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true } => CodeMapSymbolKind.Record,
        { TypeKind: TypeKind.Interface } => CodeMapSymbolKind.Interface,
        { TypeKind: TypeKind.Enum } => CodeMapSymbolKind.Enum,
        { TypeKind: TypeKind.Delegate } => CodeMapSymbolKind.Delegate,
        { TypeKind: TypeKind.Struct } => CodeMapSymbolKind.Struct,
        _ => CodeMapSymbolKind.Class,
    };

    private static string AttributeName(NameSyntax name)
    {
        var text = name switch { QualifiedNameSyntax q => q.Right.Identifier.Text, AliasQualifiedNameSyntax a => a.Name.Identifier.Text, SimpleNameSyntax s => s.Identifier.Text, _ => name.ToString() };
        return text.EndsWith("Attribute", StringComparison.Ordinal) ? text[..^"Attribute".Length] : text;
    }

    private static string Framework(string attribute) => attribute switch
    {
        "Test" or "TestCase" or "TestCaseSource" => "nunit",
        "TestMethod" or "DataTestMethod" => "mstest",
        _ => "xunit",
    };

    private static string Normalize(string path)
    {
        var value = path.Trim().Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value.TrimStart('/');
    }

    // Projects after the projects they reference.
    private static IEnumerable<Project> Order(IReadOnlyDictionary<string, Project> projects)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<Project>();
        void Visit(Project project)
        {
            if (done.Contains(project.Path) || !visiting.Add(project.Path)) return;
            foreach (var reference in project.References)
                if (projects.TryGetValue(reference, out var next)) Visit(next);
            visiting.Remove(project.Path);
            done.Add(project.Path);
            ordered.Add(project);
        }
        foreach (var project in projects.Values.OrderBy(p => p.Path, StringComparer.Ordinal)) Visit(project);
        return ordered;
    }

    private static IEnumerable<string> Closure(Project project, IReadOnlyDictionary<string, Project> projects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(project.References);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next) || !projects.TryGetValue(next, out var found)) continue;
            foreach (var reference in found.References) stack.Push(reference);
        }
        return seen.Order(StringComparer.Ordinal);
    }

    /// <summary>The .csproj and .cs files Git tracks under <paramref name="root"/>; outside a Git repository, every such file not under bin or obj.</summary>
    public static IReadOnlyList<string> TrackedFiles(string root)
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-C", root, "ls-files", "-z", "--", "*.cs", "*.csproj" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0) return [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(Path.Combine(root, f)))];
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return [.. Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(s => s is "bin" or "obj" or ".git" or "node_modules"))];
    }

    private sealed record Project(string Path, string Folder, string Name, IReadOnlyList<string> References)
    {
        public static Project Load(string root, string path)
        {
            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var document = XDocument.Load(System.IO.Path.Combine(root, path));
            var name = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value is { Length: > 0 } assembly && !assembly.Contains('$')
                ? assembly : System.IO.Path.GetFileNameWithoutExtension(path);
            var references = document.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value).OfType<string>()
                .Select(r => Normalize(System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(System.IO.Path.Combine(root, folder, r.Replace('\\', '/'))))))
                .ToList();
            return new Project(path, folder, name, references);
        }
    }
}
