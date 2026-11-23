using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.CodeMap.Roslyn;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The code map (coding packs CMP-2a): Roslyn reads a repository without building it into projects, files, types and tests,
/// keyed by SCIP symbols, and links each test to the in-repo types it touches outside its own project.
/// </summary>
public sealed class CodeMapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));

    private static readonly (string Path, string Text)[] Repository =
    [
        ("src/Store/Store.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Acme.Store</AssemblyName></PropertyGroup></Project>"""),
        ("src/Store/ModuleStore.cs", """
            namespace Acme.Store;
            public sealed class ModuleStore { public void Remove() { } public int Count => 0; private void Hidden() { } }
            public interface IStore { }
            """),
        ("src/Store/Ledger.Part1.cs", "namespace Acme.Store; public partial class Ledger { public void Close() { } }"),
        ("src/Store/Ledger.Part2.cs", "namespace Acme.Store; public partial class Ledger { public sealed record Entry(int Id); }"),
        ("tests/Store.Tests/Store.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\..\src\Store\Store.csproj" /></ItemGroup></Project>
            """),
        ("tests/Store.Tests/StoreTests.cs", """
            using Acme.Store;
            namespace Acme.Store.Tests;
            public class StoreTests
            {
                private readonly Helper _helper = new();
                private static Ledger Fresh() => new();
                [Fact] public void Remove_keeps_history() { var store = new ModuleStore(); store.Remove(); store.Remove(); }
                [Xunit.Theory] public void Ledger_closes(int n) { Fresh().Close(); }
                public void NotATest() { }
            }
            internal sealed class Helper { }
            """),
    ];

    public CodeMapTests()
    {
        foreach (var (path, text) in Repository)
        {
            var full = Path.Combine(_root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(Path.Combine(_root, "src", "Store", "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "Store", "obj", "Generated.cs"), "class Generated { }");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_repository_whose_index_lists_nothing_is_read_from_its_folder()
    {
        // A checkout that failed part-way leaves Git's index empty while the files are on disk (PACK-external, Humanizer).
        var start = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-C", _root, "init", "-q" }) start.ArgumentList.Add(argument);
        using (var git = System.Diagnostics.Process.Start(start)!) git.WaitForExit();

        var files = RoslynCodeMapReader.TrackedFiles(_root);

        files.Should().Contain(["src/Store/ModuleStore.cs", "tests/Store.Tests/StoreTests.cs", "src/Store/Store.csproj"]);
        files.Should().NotContain(f => f.Contains("/obj/", StringComparison.Ordinal));
    }

    private const string Store = "scip-dotnet nuget Acme.Store . Acme/Store/ModuleStore#";
    private const string Ledger = "scip-dotnet nuget Acme.Store . Acme/Store/Ledger#";

    [Fact]
    public void Reading_a_repository_records_projects_files_types_and_tests_by_scip_symbol()
    {
        var builder = RoslynCodeMapReader.Read(_root, RoslynCodeMapReader.TrackedFiles(_root));
        var snapshot = builder.Build("git:abc123");

        // obj/ is not part of the repository; Ledger.Entry is a nested type; StoreTests and Helper are types too.
        builder.Counts.Should().Be((2, 4, 6, 2));
        snapshot.Problems().Should().BeEmpty();
        var records = snapshot.Records.ToDictionary(r => (r.Type, r.Key));

        var store = records[(CodeMapPack.SymbolType, Store)];
        store.Properties["name"].GetString().Should().Be("ModuleStore");
        store.Properties["fullName"].GetString().Should().Be("Acme.Store.ModuleStore");
        store.Properties["kind"].GetString().Should().Be("class");
        store.Properties["members"].GetString().Should().Be("Count Remove");
        store.Source.Pack!.File.Should().Be("src/Store/ModuleStore.cs");
        records.Should().ContainKey((CodeMapPack.SymbolType, "scip-dotnet nuget Acme.Store . Acme/Store/Ledger#Entry#"));
        records[(CodeMapPack.SymbolType, "scip-dotnet nuget Acme.Store . Acme/Store/Ledger#Entry#")].Properties["kind"].GetString().Should().Be("record");

        // A partial type is one symbol, defined by each of its files.
        records[(CodeMapPack.FileType, "src/Store/Ledger.Part1.cs")].Links.Select(l => (l.Relation, l.Target))
            .Should().Contain(("CODEMAP_DEFINES", Ledger)).And.Contain(("CODEMAP_IN_PROJECT", "src/Store/Store.csproj"));
        records[(CodeMapPack.FileType, "src/Store/Ledger.Part2.cs")].Links.Should().Contain(l => l.Relation == "CODEMAP_DEFINES" && l.Target == Ledger);

        // Tests link to what they touch outside their own project (Helper and StoreTests are in the test project), and to their file.
        var remove = records[(CodeMapPack.TestType, "scip-dotnet nuget Store.Tests . Acme/Store/Tests/StoreTests#Remove_keeps_history().")];
        remove.Properties["name"].GetString().Should().Be("StoreTests.Remove_keeps_history");
        remove.Properties["framework"].GetString().Should().Be("xunit");
        // Only what the method touches itself: Ledger is used by the class's helper, not by this test.
        remove.Links.Select(l => (l.Relation, l.Target)).Should().Equal(
            ("CODEMAP_TESTS", Store), ("CODEMAP_DECLARED_IN", "tests/Store.Tests/StoreTests.cs"));
        var ledger = records[(CodeMapPack.TestType, "scip-dotnet nuget Store.Tests . Acme/Store/Tests/StoreTests#Ledger_closes().")];
        ledger.Links.First().Target.Should().Be(Ledger, "Close() on what Fresh() returns is the Ledger");
        records.Keys.Should().NotContain(k => k.Key.Contains("NotATest", StringComparison.Ordinal));
        records[(CodeMapPack.ProjectType, "tests/Store.Tests/Store.Tests.csproj")].Links.Should().ContainSingle()
            .Which.Target.Should().Be("src/Store/Store.csproj");
    }

    [Fact]
    public void The_same_code_gives_the_same_snapshot()
    {
        var first = RoslynCodeMapReader.Read(_root, RoslynCodeMapReader.TrackedFiles(_root)).Build("git:one");
        var second = RoslynCodeMapReader.Read(_root, RoslynCodeMapReader.TrackedFiles(_root)).Build("git:two");

        second.Digest.Should().Be(first.Digest);
        File.AppendAllText(Path.Combine(_root, "src/Store/ModuleStore.cs"), "\npublic enum Mode { On }\n");
        RoslynCodeMapReader.Read(_root, RoslynCodeMapReader.TrackedFiles(_root)).Build("git:three").Digest.Should().NotBe(first.Digest);
    }

    /// <summary>
    /// CMP-4: a test that reaches its subject only through a helper of its own class still tests it, in a repository that relies
    /// on what MSBuild generates (implicit usings from Directory.Build.props, InternalsVisibleTo), which the reader runs without.
    /// </summary>
    [Fact]
    public void A_test_links_what_its_own_class_helpers_touch_with_the_usings_and_internals_MSBuild_would_generate()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in new[]
            {
                ("Directory.Build.props", "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>"),
                ("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><InternalsVisibleTo Include="Lib.Tests" /></ItemGroup></Project>"""),
                ("src/Lib/Prompts.cs", "namespace Lib; public enum Mode { A } internal static class Prompts { internal static string Build(Mode m) => \"\"; }"),
                ("tests/Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>"""),
                ("tests/Lib.Tests/PromptTests.cs", """
                    using Lib;
                    namespace Lib.Tests;
                    public sealed class PromptTests
                    {
                        private static IEnumerable<(string Rung, string Prompt)> All() { yield return ("one", Prompts.Build(Mode.A)); }
                        [Fact] public void Every_rung_carries_the_setting() { foreach (var (rung, prompt) in All()) { } }
                    }
                    """),
            })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            var test = RoslynCodeMapReader.Read(root, RoslynCodeMapReader.TrackedFiles(root)).Build("git:one").Records
                .Single(r => r.Type == CodeMapPack.TestType);

            test.Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target)
                .Should().BeEquivalentTo(["scip-dotnet nuget Lib . Lib/Prompts#", "scip-dotnet nuget Lib . Lib/Mode#"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// CMP-2g (found by CMP-2c's third trial): IdentityHostContextTests is named after IdentityHostContext but reaches it only
    /// through IdentityIngestion, so the map had no test link to it and counted it untested. With
    /// <see cref="RoslynCodeMapOptions.NamedTestLinks"/>, a test class named after exactly one type outside its own project also
    /// tests that type, after what it touches; two types of that name, or the switch off, add nothing.
    /// </summary>
    [Theory]
    [InlineData(true, "HostContextTests", true)]
    [InlineData(true, "HostContextShould", true)]
    [InlineData(false, "HostContextTests", false)]
    [InlineData(true, "IngestionFlowTests", false)]
    public void A_test_class_named_after_a_type_it_reaches_through_another_tests_that_type(bool on, string testClass, bool linked)
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in new[]
            {
                ("src/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><InternalsVisibleTo Include="Core.Tests" /></ItemGroup></Project>"""),
                ("src/Core/HostContext.cs", "namespace Core; internal static class HostContext { internal static int Apply() => 1; }"),
                ("src/Core/Ingestion.cs", "namespace Core; public sealed class Ingestion { public int Run() => HostContext.Apply(); }"),
                ("tests/Core.Tests/Core.Tests.csproj", """
                    <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Core/Core.csproj" /></ItemGroup></Project>
                    """),
                ("tests/Core.Tests/Tests.cs", "namespace Core.Tests; public sealed class " + testClass + " { [Fact] public void Applies() { new Core.Ingestion().Run(); } }"),
            })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            var links = RoslynCodeMapReader.Read(root, RoslynCodeMapReader.TrackedFiles(root), new RoslynCodeMapOptions { NamedTestLinks = on })
                .Build("git:one").Records.Single(r => r.Type == CodeMapPack.TestType)
                .Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target).ToList();

            links[0].Should().Be("scip-dotnet nuget Core . Core/Ingestion#", "what the test touches comes first");
            if (linked) links.Should().Equal("scip-dotnet nuget Core . Core/Ingestion#", "scip-dotnet nuget Core . Core/HostContext#");
            else links.Should().Equal("scip-dotnet nuget Core . Core/Ingestion#");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_test_class_named_after_two_types_of_that_name_tests_neither_by_its_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in new[]
            {
                ("src/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>"""),
                ("src/Core/Ingestion.cs", "namespace Core; public sealed class Ingestion { public int Run() => 1; }"),
                ("src/Core/HostContext.cs", "namespace Core; public static class HostContext { }"),
                ("src/Other/Other.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>"""),
                ("src/Other/HostContext.cs", "namespace Other; public static class HostContext { }"),
                ("tests/Core.Tests/Core.Tests.csproj", """
                    <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Core/Core.csproj" /></ItemGroup></Project>
                    """),
                ("tests/Core.Tests/Tests.cs", "namespace Core.Tests; public sealed class HostContextTests { [Fact] public void Runs() { new Core.Ingestion().Run(); } }"),
            })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            RoslynCodeMapReader.Read(root, RoslynCodeMapReader.TrackedFiles(root), new RoslynCodeMapOptions { NamedTestLinks = true })
                .Build("git:one").Records.Single(r => r.Type == CodeMapPack.TestType)
                .Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target)
                .Should().Equal("scip-dotnet nuget Core . Core/Ingestion#");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// CMP-2f: a test project that compiles another project's files itself (<c>&lt;Compile Include="../../tools/..."&gt;</c>, as
    /// AgentMemory.Tests.Unit does with the scenario tool's scorer) links to those files' types, under the project they live in.
    /// The reader gave each file to its folder's project only, so the test's calls bound to nothing.
    /// </summary>
    [Fact]
    public void A_test_links_the_types_of_files_its_project_compiles_from_another_project()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in new[]
            {
                ("tools/Tool/Tool.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"""),
                ("tools/Tool/Program.cs", "System.Console.WriteLine(Tool.Scorer.Score());"),
                ("tools/Tool/Scorer.cs", "namespace Tool; internal static class Scorer { internal static int Score() => 1; }"),
                ("tools/Tool/Gold/Key.cs", "namespace Tool; internal sealed class Key { }"),
                ("tests/Tool.Tests/Tool.Tests.csproj", """
                    <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
                      <Compile Include="../../tools/Tool/Scorer.cs" Link="Tool/Scorer.cs" />
                      <Compile Include="../../tools/Tool/Gold/*.cs" LinkBase="Tool/Gold" />
                    </ItemGroup></Project>
                    """),
                ("tests/Tool.Tests/ScorerTests.cs", """
                    namespace Tool.Tests;
                    public sealed class ScorerTests { [Fact] public void Scores() { Tool.Scorer.Score(); _ = new Tool.Key(); } }
                    """),
            })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            var records = RoslynCodeMapReader.Read(root, RoslynCodeMapReader.TrackedFiles(root)).Build("git:one").Records;

            records.Single(r => r.Type == CodeMapPack.TestType).Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target)
                .Should().BeEquivalentTo(["scip-dotnet nuget Tool . Tool/Scorer#", "scip-dotnet nuget Tool . Tool/Key#"]);
            records.Count(r => r.Type == CodeMapPack.TestType).Should().Be(1, "a linked file adds no tests");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// CMP-2d: a test whose call goes through a NuGet package's type (an extension on a builder) links to the code it calls once
    /// the reader has the package's compile assembly, read from the obj/project.assets.json a restore leaves.
    /// </summary>
    [Fact]
    public void A_call_through_a_package_type_links_once_the_restore_s_assets_name_the_package()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The "package": an assembly in a package folder, as a restore lays it out.
            var dll = Path.Combine(root, "packages", "acme.dep", "1.0.0", "lib", "net10.0", "Acme.Dep.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            var runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
            using (var stream = File.Create(dll))
            {
                Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Acme.Dep",
                        [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("namespace Acme.Dep; public interface IBuilder { } public sealed class Builder : IBuilder { }")],
                        runtime, new(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary))
                    .Emit(stream).Success.Should().BeTrue();
            }
            var empty = new Dictionary<string, object>();
            var assets = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = 3,
                ["targets"] = new Dictionary<string, object> { ["net10.0"] = new Dictionary<string, object>
                {
                    ["Acme.Dep/1.0.0"] = new Dictionary<string, object>
                    {
                        ["type"] = "package", ["compile"] = new Dictionary<string, object> { ["lib/net10.0/Acme.Dep.dll"] = empty },
                    },
                } },
                ["libraries"] = new Dictionary<string, object>
                {
                    ["Acme.Dep/1.0.0"] = new Dictionary<string, object> { ["type"] = "package", ["path"] = "acme.dep/1.0.0" },
                },
                ["packageFolders"] = new Dictionary<string, object> { [Path.Combine(root, "packages") + Path.DirectorySeparatorChar] = empty },
            });
            foreach (var (path, text) in new[]
            {
                ("src/Lib/Lib.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>"""),
                ("src/Lib/obj/project.assets.json", assets),
                ("src/Lib/BuilderExtensions.cs", "namespace Lib; public static class BuilderExtensions { public static Acme.Dep.IBuilder AddLib(this Acme.Dep.IBuilder b) => b; }"),
                ("tests/Lib.Tests/Lib.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>"""),
                ("tests/Lib.Tests/obj/project.assets.json", assets),
                ("tests/Lib.Tests/LibTests.cs", "using Lib; namespace Lib.Tests; public sealed class LibTests { [Fact] public void Adds() { new Acme.Dep.Builder().AddLib(); } }"),
            })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            // TrackedFiles leaves bin and obj out; the assets file is read beside each project.
            string[] Links() => [.. RoslynCodeMapReader.Read(root, RoslynCodeMapReader.TrackedFiles(root)).Build("git:one").Records
                .Single(r => r.Type == CodeMapPack.TestType).Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target)];

            Links().Should().Equal("scip-dotnet nuget Lib . Lib/BuilderExtensions#");

            File.Delete(Path.Combine(root, "tests", "Lib.Tests", "obj", "project.assets.json"));
            Links().Should().BeEmpty("without the package the call does not bind");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_long_scip_identity_keeps_a_readable_key_with_a_hash()
    {
        var scip = "scip-dotnet nuget Acme . " + string.Concat(Enumerable.Repeat("Namespace/", 30)) + "Type#";
        var key = CodeMapBuilder.Key(scip);

        key.Length.Should().Be(CodeMapBuilder.MaximumKeyLength);
        key.Should().StartWith("scip-dotnet nuget Acme . Namespace/");
        CodeMapBuilder.Key(scip + "Other#").Should().NotBe(key);
        CodeMapBuilder.Key(Store).Should().Be(Store);
    }

    [Fact]
    public void The_code_map_manifest_validates_and_walks_from_files_and_symbols_to_tests()
    {
        var manifest = CodeMapPack.Manifest;

        manifest.Validate().Should().BeEmpty();
        manifest.Id.Should().Be(CodeMapPack.Id);
        manifest.Traversal!.Hubs.Should().BeEquivalentTo([CodeMapPack.ProjectType, CodeMapPack.FileType, CodeMapPack.SymbolType]);
        manifest.Traversal.Relations.Select(r => (r.Name, r.Direction)).Should().Contain(("CODEMAP_TESTS", ModuleTraversalDirection.In));
    }

    /// <summary>CMP-2c: types that use each other link in a cycle; a snapshot import orders them targets first where it can.</summary>
    [Fact]
    public void Types_that_use_each_other_import_in_an_order_a_snapshot_allows_and_a_seed_refuses()
    {
        var builder = new CodeMapBuilder();
        builder.AddFile("src/Use.cs", null);
        foreach (var name in new[] { "A", "B", "C", "D" })
            builder.AddSymbol("scip-dotnet nuget Acme . Acme/" + name + "#", name, "Acme." + name, CodeMapSymbolKind.Class, "Acme", "src/Use.cs", 1);
        builder.AddUses("scip-dotnet nuget Acme . Acme/A#", ["scip-dotnet nuget Acme . Acme/B#", "scip-dotnet nuget Acme . Acme/A#", "unknown#"]);
        builder.AddUses("scip-dotnet nuget Acme . Acme/B#", ["scip-dotnet nuget Acme . Acme/A#"]);
        builder.AddUses("scip-dotnet nuget Acme . Acme/C#", ["scip-dotnet nuget Acme . Acme/D#"]);
        var snapshot = builder.Build("git:one");

        snapshot.Records.Single(r => r.Key.EndsWith("/A#", StringComparison.Ordinal)).Links.Select(l => (l.Relation, l.Target))
            .Should().Equal([("CODEMAP_USES", "scip-dotnet nuget Acme . Acme/B#")], "itself and what the map does not hold are dropped");
        ((Action)(() => snapshot.ImportOrder())).Should().Throw<InvalidOperationException>().WithMessage("*cycle*");
        // Ready records first (D, then C, which uses it); A and B wait on each other, so the first by key breaks the cycle.
        snapshot.ImportOrder(selfCycles: true).Where(r => r.Type == CodeMapPack.SymbolType).Select(r => r.Key[^2..])
            .Should().Equal("D#", "C#", "A#", "B#");
    }
}
