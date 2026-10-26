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
}
