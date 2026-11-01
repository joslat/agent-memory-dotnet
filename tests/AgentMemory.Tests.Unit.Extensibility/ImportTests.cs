#pragma warning disable AMEXT001
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 15: another tool's memory export imported as a signed knowledge pack of notes.</summary>
public sealed class ImportTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private static PackTrust Trust() => PackTrust.Parse(Encoding.UTF8.GetBytes(PackAuthoring.TrustPolicy(Key.PublicKeyPem, "com.example")));

    [Fact]
    public void A_mem0_export_becomes_a_pack_of_notes_that_names_its_source_and_says_what_it_dropped()
    {
        var export = Encoding.UTF8.GetBytes("""
            {"results":[
              {"id":"m1","memory":"Prefers tea to coffee","hash":"h1","created_at":"2026-10-01T09:30:00-07:00","user_id":"u1"},
              {"id":"m2","memory":"Café on Fridays","hash":"h2","created_at":"2026-10-02T09:30:00Z","metadata":{"source":"chat"}},
              {"id":"m3","memory":"","hash":"h3"},
              {"id":"m1","memory":"A second m1","hash":"h4"}]}
            """);
        var mapping = ImportMapping.Read(File.ReadAllBytes(Path.Combine(Root(), "tools", "Ampack", "mappings", "mem0.json")));

        var result = PackImports.FromJson(export, mapping, "com.example.imported-memories", "1.0.0", "Example Owner", Key.PrivateKeyPem);

        result.Records.Should().Be(2);
        result.Dropped.Should().ContainKey("hash").And.ContainKey("user_id").And.ContainKey("metadata");
        result.Skipped.Should().Contain(s => s.Contains("no text")).And.Contain(s => s.Contains("repeated key"));
        var pack = PackLoader.Load(result.Archive, Trust());
        pack.HasDerivation.Should().BeTrue();
        pack.Card!.Provenance.Should().Contain("Imported from mem0");
        var notes = pack.Knowledge!.Proposals;
        notes.Select(n => n.Key).Should().Equal("m1", "m2");
        notes[1].Properties["text"].GetString().Should().Be("Café on Fridays", "imported text is normalised to NFC");
        notes[0].Life!.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 10, 1, 16, 30, 0, TimeSpan.Zero));
        var derivation = Encoding.UTF8.GetString(PackReader.ReadArchive(result.Archive, "import").Files["META/derivation.json"]);
        derivation.Should().Contain("sha256:" + Convert.ToHexString(SHA256.HashData(export)).ToLowerInvariant()).And.Contain("\"import\"");
    }

    [Fact]
    public void An_assistant_export_imports_one_note_a_statement_keyed_by_its_text()
    {
        var export = Encoding.UTF8.GetBytes("Lives in Lyon\n\nWorks on memory packs\nLives in Lyon\n");
        var result = PackImports.FromText(export, "ChatGPT", "com.example.chat-memory", "1.0.0", "Example Owner", Key.PrivateKeyPem);
        result.Records.Should().Be(2);
        result.Skipped.Should().ContainSingle(s => s.Contains("repeated key"));
        PackLoader.Load(result.Archive, Trust()).Knowledge!.Proposals.Select(p => p.Properties["text"].GetString()).Should().Equal("Lives in Lyon", "Works on memory packs");
        FluentActions.Invoking(() => PackImports.FromText("\n\n"u8.ToArray(), "x", "com.example.empty", "1.0.0", "o", Key.PrivateKeyPem)).Should().Throw<InvalidDataException>();
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "Ampack", "mappings", "mem0.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root is not above the test's folder");
    }
}
