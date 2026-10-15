using System.Runtime.Loader;
using System.Text;
using AgentMemory.Extensibility.Neo4j.Migrations;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleMigrationScriptReaderTests
{
    [Fact]
    public void ReadsRealEmbeddedScriptsWithoutLoadingImplementation()
    {
        var image = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ModuleFixtures", "ModuleDiscoveryFixture.dll"));
        var scripts = ModuleMigrationScripts.LoadFromAssemblyImage(image);
        scripts.Select(script => script.Key).Should().Equal("0001_due_index", "0002_note");
        scripts.Should().OnlyContain(script => script.ModuleId == "com.example.discovery");
        scripts.Should().OnlyContain(script => script.Sha256 == ModuleMigrationDefinitions.Hash(script.Statement));
        scripts[1].Statement.Should().Contain("https://example.org/a;b");
        AssemblyLoadContext.Default.Assemblies.Should().NotContain(assembly => assembly.GetName().Name == "ModuleDiscoveryFixture");
    }

    private const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.discovery","version":"0.1.0","sdk":"[0.1,0.2)"}
        """;
    private const string Source = "MATCH (n:ReviewItem) SET n.note = 'ready';";
    private static (string, byte[]) Entry(string name, string source) => (name, Encoding.UTF8.GetBytes(source));
    private static byte[] Image(params (string, byte[])[] scripts) => ModuleAssemblyTests.Image("0.1.0",
        new[] { Entry("fixture.agentmemory-module.json", Manifest) }.Concat(scripts).ToArray());

    [Fact]
    public void SortsScriptsByStableKeyAndPreservesOriginalSource()
    {
        var original = "// whole artifact\r\n" + Source + "\r\n";
        var steps = ModuleMigrationScripts.LoadFromAssemblyImage(Image(
            Entry("Migrations/0002_second.cypher", Source),
            Entry("fixture.Migrations.0001_first.cypher", original), Entry("unrelated.txt", "ignored")));
        steps.Select(step => step.Key).Should().Equal("0001_first", "0002_second");
        steps[0].Statement.Should().Be(original);
        steps[0].Sha256.Should().Be(ModuleMigrationDefinitions.Hash(original));
        ((System.Collections.IList)steps).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void NoScriptsIsValidButMissingOrMismatchedManifestIsNot()
    {
        ModuleMigrationScripts.LoadFromAssemblyImage(Image()).Should().BeEmpty();
        foreach (var image in new[] {
            ModuleAssemblyTests.Image("0.1.0", Entry("Migrations.0001_first.cypher", Source)),
            ModuleAssemblyTests.Image("0.2.0", Entry("fixture.agentmemory-module.json", Manifest)) })
            FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(image)).Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("fixture.Migrations.first.cypher")]
    [InlineData("fixture.Migrations.0001_first.sql")]
    [InlineData("Migrations/../0001_first.cypher")]
    [InlineData("Migrations/0001_first.extra.cypher")]
    public void RefusesMalformedMigrationResourceNames(string name) =>
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(Entry(name, Source))))
            .Should().Throw<FormatException>();

    [Fact]
    public void RefusesDuplicateKeysAcrossResourcePrefixes() =>
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(
            Entry("a.Migrations.0001_first.cypher", Source), Entry("b.Migrations.0001_first.cypher", Source))))
            .Should().Throw<FormatException>().WithMessage("*duplicate migration key*");

    [Fact]
    public void DoesNotFollowLinkedMigrationFiles()
    {
        var name = "fixture.Migrations.0001_first.cypher";
        var image = ModuleAssemblyTests.Image("0.1.0", new[] {
            Entry("fixture.agentmemory-module.json", Manifest), Entry(name, Source) }, linkedResource: name);
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(image))
            .Should().Throw<FormatException>().WithMessage("*embedded*externally*");
    }

    [Theory]
    [InlineData("// comment only")]
    [InlineData("\uFEFFMATCH (n:ReviewItem) RETURN n;")]
    [InlineData("MATCH (n:ReviewItem) SET n.x = 'unfinished")]
    public void RefusesEmptyOrMalformedSources(string source) =>
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(Entry("Migrations.0001_first.cypher", source))))
            .Should().Throw<FormatException>();

    [Fact]
    public void RefusesInvalidUtf8AndOversizeScripts()
    {
        foreach (var bytes in new[] { new byte[] { 0xff, 0xfe }, new byte[256 * 1024 + 1] })
            FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(("Migrations.0001_first.cypher", bytes))))
                .Should().Throw<FormatException>();
    }

    [Fact]
    public void BoundsCountAndAggregateBytes()
    {
        var many = Enumerable.Range(1, 129).Select(i => Entry($"Migrations.{i:0000}_step.cypher", Source)).ToArray();
        var large = Enumerable.Range(1, 5).Select(i => Entry($"Migrations.{i:0000}_step.cypher", Source.PadRight(256 * 1024))).ToArray();
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(many)))
            .Should().Throw<FormatException>().WithMessage("*128*");
        FluentActions.Invoking(() => ModuleMigrationScripts.LoadFromAssemblyImage(Image(large)))
            .Should().Throw<FormatException>().WithMessage("*1 MiB*");
    }

    [Fact]
    public void PinsOneImageForBothManifestAndScriptDiscovery()
    {
        var first = Image(Entry("Migrations.0001_first.cypher", Source));
        var second = Image(Entry("Migrations.0001_first.cypher", Source.Replace("ready", "other", StringComparison.Ordinal)));
        using var memory = new ChangingImage(first, second);
        ModuleMigrationScripts.LoadFromAssemblyImage(memory.Image)[0].Statement.Should().Be(Source);
    }

    private sealed class ChangingImage(byte[] first, byte[] second) : System.Buffers.MemoryManager<byte>
    {
        private int _reads;
        internal ReadOnlyMemory<byte> Image => CreateMemory(first.Length);
        public override Span<byte> GetSpan() => ++_reads == 1 ? first : second;
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
