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

    [Fact]
    public void A_renamed_file_keeps_its_old_path_as_a_note_that_ends_where_the_rename_begins()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["release-plan.md"] = Encoding.UTF8.GetBytes("The plan, now dated.") };
        var commits = new Dictionary<string, DateTimeOffset>
        {
            ["plan.md"] = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), ["release-plan.md"] = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero),
        };
        var renames = new[] { new FolderRename("plan.md", "release-plan.md", new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero), Encoding.UTF8.GetBytes("The plan.")) };

        var result = PackImports.FromFolder(files, commits, "com.example.folder", "1.0.0", "Example Owner", Key.PrivateKeyPem, renames);
        result.Records.Should().Be(2);
        var records = PackLoader.Load(result.Archive, Trust()).Knowledge!.Proposals.ToDictionary(p => p.Key);
        records["plan.md"].Properties["text"].GetString().Should().Be("The plan.");
        records["plan.md"].Life!.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        records["plan.md"].Life!.ValidToUtc.Should().Be(new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero), "the old path ends where the rename begins");
        records["release-plan.md"].Supersedes.Should().Equal("plan.md");

        // A path in use again keeps no history: the file there is its own note.
        files["plan.md"] = Encoding.UTF8.GetBytes("A new plan at the old path.");
        PackImports.FromFolder(files, commits, "com.example.folder", "1.0.0", "Example Owner", Key.PrivateKeyPem, renames)
            .Skipped.Should().Contain(s => s.Contains("in use again"));
    }

    [Fact]
    public void A_memory_folder_imports_one_note_a_file_with_strict_front_matter_and_dates_from_git()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["people/ana.md"] = Encoding.UTF8.GetBytes("---\nvalidFrom: 2026-09-01\nrole: \"lead\"\n---\nAna leads [[projects/atlas]] and knows [[bob]].\n"),
            ["projects/atlas.md"] = Encoding.UTF8.GetBytes("Atlas ships in November.\r\n"),
            ["anchor.md"] = Encoding.UTF8.GetBytes("---\nbase: &a x\n---\nRefused.\n"),
            ["nested.md"] = Encoding.UTF8.GetBytes("---\nowner:\n  name: x\n---\nRefused.\n"),
            ["empty.md"] = Encoding.UTF8.GetBytes("---\ntitle: nothing\n---\n"),
            ["notes.txt"] = Encoding.UTF8.GetBytes("not markdown"),
        };
        var commits = new Dictionary<string, DateTimeOffset> { ["projects/atlas.md"] = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero), ["people/ana.md"] = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        var result = PackImports.FromFolder(files, commits, "com.example.memory-folder", "1.0.0", "Example Owner", Key.PrivateKeyPem);

        result.Records.Should().Be(2);
        result.Skipped.Should().Contain(s => s.StartsWith("'anchor.md': front matter refused")).And.Contain(s => s.StartsWith("'nested.md': front matter refused"))
            .And.Contain(s => s.StartsWith("'empty.md': no text")).And.Contain(s => s.Contains("'bob'"));
        result.Skipped.Should().NotContain(s => s.Contains("projects/atlas'"), "a link to a note the folder holds is not reported");
        result.Dropped.Should().ContainKey("front matter 'role'");
        var notes = PackLoader.Load(result.Archive, Trust()).Knowledge!.Proposals;
        notes.Select(n => n.Key).Should().Equal("people/ana.md", "projects/atlas.md");
        notes[0].Properties["text"].GetString().Should().Be("Ana leads [[projects/atlas]] and knows [[bob]].");
        notes[0].Life!.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "front matter wins over git");
        notes[1].Life!.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero), "a file that names no date dates from its first commit");
    }

    [Fact]
    public void Review_15_17_an_import_never_writes_a_pack_the_loader_refuses_and_says_what_it_could_not_use()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            // F3: a byte order mark before the front matter
            ["bom.md"] = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("---\nvalidFrom: 2026-09-01\n---\nWith a BOM.\n")],
            // F2: ended before git first saw it
            ["ended.md"] = Encoding.UTF8.GetBytes("---\nvalidTo: 2026-10-01\n---\nNo longer true.\n"),
            // F1: longer than a record line holds
            ["big.md"] = Encoding.UTF8.GetBytes(new string('x', 70_000)),
            [new string('k', 210) + ".md"] = Encoding.UTF8.GetBytes("A key too long."),
        };
        var commits = new Dictionary<string, DateTimeOffset> { ["ended.md"] = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) };
        var result = PackImports.FromFolder(files, commits, "com.example.edges", "1.0.0", "Example Owner", Key.PrivateKeyPem);
        result.Skipped.Should().Contain(s => s.StartsWith("'big.md'")).And.Contain(s => s.Contains("1 to 200 characters"));
        var notes = PackLoader.Load(result.Archive, Trust()).Knowledge!.Proposals;
        notes.Select(n => n.Key).Should().Equal("bom.md", "ended.md");
        notes[0].Properties["text"].GetString().Should().Be("With a BOM.");
        notes[0].Life!.ValidFromUtc.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        notes[1].Life!.ValidFromUtc.Should().BeNull("a git date after the note's end is left out");

        // F8 and F9: unusable mapped values are reported; ISO 8601 only; the mapping is in the derivation.
        var export = Encoding.UTF8.GetBytes("""{"results":[{"id":{"x":1},"memory":"One","created_at":"07/08/2026"},{"id":"b","memory":"Two","created_at":1730000000}]}""");
        var mapping = ImportMapping.Read(File.ReadAllBytes(Path.Combine(Root(), "tools", "Ampack", "mappings", "mem0.json")));
        var json = PackImports.FromJson(export, mapping, "com.example.unusable", "1.0.0", "Example Owner", Key.PrivateKeyPem);
        json.Dropped.Should().Contain(new KeyValuePair<string, int>("unusable 'created_at'", 2)).And.Contain(new KeyValuePair<string, int>("unusable 'id'", 1));
        Encoding.UTF8.GetString(PackReader.ReadArchive(json.Archive, "import").Files["META/derivation.json"]).Should().Contain("\"mapping.text\":\"memory\"");
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "Ampack", "mappings", "mem0.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root is not above the test's folder");
    }
}
