#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// One snapshot, two stores (review 3): the same records walked from Neo4j and from memory give the same offers in the same
/// order, with the same scores and reasons, including names and paths outside ASCII (folded as the format says: ASCII only),
/// ties broken by key, a hub of tied tests, and anchors that differ only in ASCII case, dots and slashes.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static ModuleSnapshot CrossStoreSnapshot()
    {
        var builder = new CodeMapBuilder();
        builder.AddProject("src/Cross/Cross.csproj", "Acme.Cross");
        string[] files = ["src/Cross/CrossStore.cs", "src/Cross/Über.cs", "src/Cross/über.cs", "docs/cross-µs.cs", "docs/cross-μs.cs", "src/Cross/ΣTATE.cs", "src/Cross/İndex.cs"];
        foreach (var file in files) builder.AddFile(file, "src/Cross/Cross.csproj");
        builder.AddFile("tests/CrossTests.cs", null);
        for (var f = 0; f < files.Length; f++)
        {
            var symbol = $"scip-dotnet nuget Acme.Cross . Acme/Cross/Type{f}#";
            builder.AddSymbol(symbol, "Type" + f, "Acme.Cross.Type" + f, CodeMapSymbolKind.Class, "Acme.Cross", files[f], 1, ["Remove", "Close"]);
            // Twelve tests of each type, every one alike: which few a fan-out of eight keeps is decided by key alone.
            for (var t = 0; t < 12; t++)
                builder.AddTest($"scip-dotnet nuget Acme.Cross.Tests . Acme/Cross/Tests/CrossTests#T{f}x{t:00}().", $"CrossTests.T{f}x{t:00}",
                    "tests/CrossTests.cs", 10 + f * 20 + t, "xunit", [symbol]);
        }
        return builder.Build("git:cross-store");
    }

    [Fact]
    public async Task OneSnapshotWalkedFromNeo4jAndFromMemoryOffersTheSameRecordsInTheSameOrder()
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(30);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-1));
        try
        {
            var snapshot = CrossStoreSnapshot();
            await store.ImportAsync(snapshot, at);
            using var services = Policies();
            var neo4j = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(CodeMapPack.Manifest),
                services.GetRequiredService<IMemoryIsolationPolicy>());
            var memory = PackMemory.From([], [snapshot]);

            string[][] requests =
            [
                ["src/Cross/CrossStore.cs"],
                ["SRC/cross/crossstore.CS", "./src\\Cross/CrossStore.cs"],
                ["src/Cross/Über.cs"],
                ["src/Cross/über.cs"],
                ["docs/cross-µs.cs"],
                ["src/Cross/ΣTATE.cs", "src/Cross/İndex.cs"],
                ["src/Cross/CrossStore.cs "],
            ];
            foreach (var anchors in requests)
            {
                var request = new ContextRequest
                {
                    SessionId = "s", Owner = "lea", Query = "remove close state", Anchors = [.. anchors.Select(a => new ContextAnchor(ContextAnchorKind.Path, a))],
                };
                var fromNeo4j = await TraversalWalk.RunAsync(KnowledgePack.ResolutionOf(CodeMapPack.Manifest), neo4j, request, CancellationToken.None);
                var fromMemory = await TraversalWalk.RunAsync(memory.Resolution, memory, request, CancellationToken.None);

                fromNeo4j.Problems.Should().BeEmpty(string.Join(" | ", anchors));
                fromNeo4j.Items.Select(i => (i.Record.Type, i.Record.Key, Math.Round(i.Score, 9), i.Via, i.Hops))
                    .Should().Equal(fromMemory.Items.Select(i => (i.Record.Type, i.Record.Key, Math.Round(i.Score, 9), i.Via, i.Hops)), string.Join(" | ", anchors));
            }

            // ASCII folding only: a path that differs outside ASCII is another file in both stores.
            var umlaut = await TraversalWalk.RunAsync(memory.Resolution, memory,
                new ContextRequest { SessionId = "s", Owner = "lea", Query = "", Anchors = [new ContextAnchor(ContextAnchorKind.Path, "src/Cross/Über.cs")] }, CancellationToken.None);
            umlaut.Items.Where(i => i.Record.Type == CodeMapPack.FileType).Select(i => i.Record.Key).Should().ContainSingle();
        }
        finally { await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(1)); }
    }
}
