#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Work;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Root RA, review 5: the guard that finds the next divergence between the two stores by itself. Seeded random code maps in two
/// versions (the second removes files, renames symbols and changes tests, so Neo4j holds links to older versions and closed
/// keys), a work snapshot whose items link to files the second version partly removes, names in several scripts and normal
/// forms; random anchors in case, slash and './' variants with random question words. Every walk runs from Neo4j and from memory,
/// and every offer and every problem must be the same. Rounds 2 to 4 found their walk highs by reading; this finds such a
/// difference by running.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static readonly string[] DiffParts = ["Map", "Store", "Remove", "Close", "Pack", "Walk", "Key", "Hub", "Gate", "Index", "\u00DCber", "U\u0308ber", "\u00B5s", "\u03BCs", "\u0130ndex", "\u03A3tate", "Na\u00EFve", "S\u00E3o"];

    private static string Pick(Random rng, string[] from) => from[rng.Next(from.Length)];

    private sealed record DiffMap(ModuleSnapshot Snapshot, List<string> Files, List<string> Symbols, List<string> Names);

    // A code map for the seed: two projects, files, symbols with member words, tests of one to four symbols, one hub symbol.
    private static DiffMap RandomMap(int seed, Random rng, string source, ISet<string>? dropFiles = null, int rename = -1)
    {
        var builder = new CodeMapBuilder();
        var root = $"src/Diff{seed}";
        string[] projects = [$"{root}/A/A.csproj", $"{root}/B/B.csproj"];
        builder.AddProject(projects[0], $"Acme.Diff{seed}.A");
        builder.AddProject(projects[1], $"Acme.Diff{seed}.B");
        var files = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var project = projects[i % 2];
            var file = $"{root}/{(i % 2 == 0 ? "A" : "B")}/{DiffParts[(seed + i) % DiffParts.Length]}{DiffParts[(seed * 7 + i * 3) % DiffParts.Length]}{i}.cs";
            if (dropFiles?.Contains(file) == true) continue;
            builder.AddFile(file, project);
            files.Add(file);
        }
        builder.AddFile($"tests/Diff{seed}/Tests.cs", null);
        var symbols = new List<string>();
        var names = new List<string>();
        var index = 0;
        foreach (var file in files)
            for (var k = 0; k < 1 + (index % 3); k++, index++)
            {
                var name = DiffParts[(seed + index) % DiffParts.Length] + (index == rename ? "Renamed" : "") + index;
                // Every fifth key holds a character above U+D800 (astral or private use), where code-point and UTF-16 order differ.
                var tail = index % 5 == 0 ? (index % 10 == 0 ? "\U0001F600" : "\uE000") : "";
                var scip = $"scip-dotnet nuget Acme.Diff{seed} . Acme/Diff{seed}/{name}{tail}#";
                builder.AddSymbol(scip, name, $"Acme.Diff{seed}.{name}", CodeMapSymbolKind.Class, $"Acme.Diff{seed}.{(file.Contains("/A/") ? "A" : "B")}", file, 1,
                    [Pick(rng, DiffParts), Pick(rng, DiffParts)]);
                symbols.Add(scip);
                names.Add(name);
            }
        for (var t = 0; t < 30; t++)
        {
            var tested = Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => t % 6 == 0 ? symbols[0] : symbols[rng.Next(symbols.Count)]).Distinct().ToList();
            builder.AddTest($"scip-dotnet nuget Acme.Diff{seed}.Tests . Acme/Diff{seed}/Tests#T{t:00}{Pick(rng, DiffParts)}().",
                $"Tests.T{t:00}{Pick(rng, DiffParts)}", $"tests/Diff{seed}/Tests.cs", 10 + t, "xunit", tested);
        }
        return new(builder.Build(source), files, symbols, names);
    }

    private static string Variant(Random rng, string path) => rng.Next(4) switch
    {
        0 => path,
        1 => "./" + path.Replace('/', '\\'),
        2 => path.ToUpperInvariant(),
        _ => " /" + path + "\t",
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RandomSnapshotsWalkTheSameFromNeo4jAndFromMemory(int seed)
    {
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(50 + seed);
        await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(-3));
        await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(-2));
        try
        {
            var rng = new Random(seed);
            var first = RandomMap(seed, new Random(seed * 31), $"git:diff{seed}-one");
            await store.ImportAsync(first.Snapshot, at);
            // Work items link to files of the first version; the second removes two of them.
            var ledger = "{\"tasks\":[" + string.Join(",", Enumerable.Range(0, 4).Select(i => $"{{\"id\":\"DIFF{seed}-{i}\",\"task\":\"{Pick(rng, DiffParts)} work\",\"status\":\"Done\",\"notes\":\"n\"}}")) + "]}";
            var commits = Enumerable.Range(0, 4).Select(i => new WorkCommit($"d{seed}{i}", $"DIFF{seed}-{i}: change", [.. first.Files.Where((_, f) => (f + i) % 3 == 0)])).ToList();
            var work = WorkLedgerReader.Read(ledger, "tasks.json", commits, $"git:diff{seed}-one");
            await store.ImportAsync(work, at.AddSeconds(1));
            var second = RandomMap(seed, new Random(seed * 31), $"git:diff{seed}-two", dropFiles: new HashSet<string>(first.Files.Take(2)), rename: 3);
            await store.ImportAsync(second.Snapshot, at.AddSeconds(2));

            using var services = Policies();
            var neo4j = new Neo4jModuleTraversalReader(fixture.TransactionRunner, work.Resolve(), services.GetRequiredService<IMemoryIsolationPolicy>());
            var memory = PackMemory.From([], [second.Snapshot, work]);
            for (var request = 0; request < 12; request++)
            {
                var anchors = Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => rng.Next(4) switch
                {
                    0 => new ContextAnchor(ContextAnchorKind.Path, Variant(rng, Pick(rng, [.. first.Files]))),
                    1 => new ContextAnchor(ContextAnchorKind.Symbol, Pick(rng, [.. second.Names])),
                    2 => new ContextAnchor(ContextAnchorKind.Symbol, Pick(rng, [.. second.Symbols])),
                    _ => new ContextAnchor(ContextAnchorKind.Path, $"src/Diff{seed}/A/"),
                }).ToList();
                var question = string.Join(' ', Enumerable.Range(0, rng.Next(4)).Select(_ => Pick(rng, DiffParts)));
                var context = new ContextRequest { SessionId = "s", Owner = "lea", Query = question, Anchors = anchors };
                var fromNeo4j = await TraversalWalk.RunAsync(work.Resolve(), neo4j, context, CancellationToken.None);
                var fromMemory = await TraversalWalk.RunAsync(memory.Resolution, memory, context, CancellationToken.None);
                var because = $"seed {seed}, request {request}: [{string.Join(" | ", anchors.Select(a => a.Kind + ":" + a.Value))}] '{question}'";
                static string Offers(TraversalResult walk) => string.Join("\n", walk.Items.Select(i => $"{i.Record.Type} '{i.Record.Key}' {Math.Round(i.Score, 9)} {i.Via} {i.Hops}"))
                    + "\n-- problems: " + string.Join(" | ", walk.Problems);
                var (n, m) = (Offers(fromNeo4j), Offers(fromMemory));
                if (n != m)
                {
                    var keys = fromNeo4j.Items.Select(i => i.Record.Key).ToHashSet();
                    keys.SymmetricExceptWith(fromMemory.Items.Select(i => i.Record.Key));
                    var stored = await fixture.TransactionRunner.ReadAsync(async runner => (await (await runner.RunAsync(
                        "UNWIND $keys AS key MATCH (v {key:key}) OPTIONAL MATCH (v)-[r]->(t) WHERE t.key IS NOT NULL "
                        + "WITH key, v, collect(type(r) + '>' + t.key + (CASE WHEN t.invalidated_at IS NULL THEN '' ELSE ' (closed)' END)) AS links "
                        + "RETURN key + ' v' + v.id + (CASE WHEN v.invalidated_at IS NULL THEN ' live' ELSE ' closed' END) + ' ' + reduce(s = '', l IN links | s + l + ', ') AS line ORDER BY line",
                        new { keys = keys.ToList() })).ToListAsync(r => ValueExtensions.As<string>(r["line"]))));
                    var held = string.Join("\n", second.Snapshot.Records.Concat(work.Records).Where(r => keys.Contains(r.Key))
                        .Select(r => r.Key + " " + string.Join(", ", r.Links.Select(l => l.Relation + ">" + l.Target))));
                    throw new Xunit.Sdk.XunitException($"{because}\n== Neo4j ==\n{n}\n== memory ==\n{m}\n== stored in Neo4j ==\n{string.Join("\n", stored)}\n== held in memory ==\n{held}");
                }
            }
        }
        finally
        {
            await store.ImportAsync(NoWork("git:clear"), at.AddSeconds(3));
            await store.ImportAsync(NoCodeMap("git:clear"), at.AddSeconds(4));
        }
    }
}
