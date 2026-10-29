#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Root RA, review 5: one intake for every entry point, and stored text in one normal form. Each test here failed before its
/// root fix: the record's constructor and the factories gave the walk different anchors, and a record written with a decomposed
/// file name could never be reached.
/// </summary>
public sealed class OneIntakeTests
{
    private static readonly string[] Raw =
    [
        "./src\\A.cs", "src/A.cs", " src/A.cs\t", "/src/A.cs", "./ a", "src/U\u0308ber.cs", "src/\u00DCber.cs",
        "\u00A0", "   ", new string('x', 3000), "src/\uD800.cs", "./src//B.cs",
    ];

    [Fact]
    public void The_constructor_and_the_factories_give_the_walk_the_same_anchors_and_the_intake_is_idempotent()
    {
        var byConstructor = ContextAnchors.Taken(Raw.Select(v => new ContextAnchor(ContextAnchorKind.Path, v)));
        var byFactory = ContextAnchors.Taken(Raw.Select(v => ContextAnchor.Path(v)));

        byConstructor.Select(a => (a.Kind, a.Value)).Should().Equal(byFactory.Select(a => (a.Kind, a.Value)));
        ContextAnchors.Taken(byConstructor).Select(a => a.Value).Should().Equal(byConstructor.Select(a => a.Value), "a taken anchor is taken as it is");
        byConstructor.Select(a => a.Value).Should().Equal("src/A.cs", "a", "src/\u00DCber.cs", "\u00A0", "src//B.cs");
        ContextAnchors.Taken([new ContextAnchor(ContextAnchorKind.Symbol, "  RemovePackAsync \t")]).Single().Value.Should().Be("RemovePackAsync");
    }

    [Fact]
    public async Task A_record_read_with_a_decomposed_name_is_stored_in_NFC_and_reached()
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Map/Map.csproj", "Acme.Map");
        builder.AddFile("src/Map/U\u0308ber.cs", "src/Map/Map.csproj");
        var snapshot = builder.Build("git:nfd");
        snapshot.Records.Should().Contain(r => r.Key == "src/Map/\u00DCber.cs", "a snapshot's records are taken in NFC as they are set");

        var memory = PackMemory.From([], [snapshot]);
        foreach (var anchor in new[] { "src/Map/U\u0308ber.cs", "src/Map/\u00DCber.cs" })
        {
            var walk = await TraversalWalk.RunAsync(memory.Resolution, memory, new ContextRequest
            {
                SessionId = "s", Owner = "lea", Query = "", Anchors = [new ContextAnchor(ContextAnchorKind.Path, anchor)],
            }, CancellationToken.None);
            walk.Items.Should().Contain(i => i.Record.Key == "src/Map/\u00DCber.cs", anchor);
        }
    }

    [Fact]
    public void Admission_refuses_text_that_is_not_in_NFC()
    {
        var file = new ModuleProposal { Type = CodeMapPack.FileType, Key = "src/U\u0308ber.cs", Source = new() { Pack = new() { Id = "p", Version = "1.0.0", Digest = "sha256:" + new string('a', 64), File = "records/files.jsonl", Line = 1 }, Excerpt = "x" } };
        ModuleProposalAdmission.AdmitSeed(CodeMapPack.Manifest, file).Should().Contain("key: text must be in Unicode NFC.");
    }
}
