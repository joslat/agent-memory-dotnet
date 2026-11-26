using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// The Identity Kinds Benchmark, tier 1, on the repository's own code (IKB-1): 134 scripted cases in each of three arms,
/// each case's verdict compared with the recorded baseline (version 2: each arm on an empty store; 2.3.1's in its about note). No model is called.
/// The pass rule that holds whatever the baseline says: an arm with evidence-based identity merges no two real things.
/// A change that moves a row, for the better or the worse, updates the baseline file with its reason.
/// </summary>
[Collection("Identity Kinds Neo4j")]
[Trait("Category", "Integration")]
public sealed class IdentityKindsBenchmarkTests(IdentityKindsNeo4jFixture fixture, ITestOutputHelper output)
{
    private static string BaselinePath([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../../AgentMemory.Tests.Unit/TestData/Identity/identity-kinds-tier1-baseline.json"));

    public static IEnumerable<object[]> Arms() => IdentityKindsBenchmark.Arms.Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task Tier1_reproduces_the_recorded_baseline_case_by_case(string arm)
    {
        // What an arm run before this one left in the store (each arm now starts on an empty store; ER-KIT).
        await using (var session = fixture.Driver.AsyncSession())
        {
            var left = await (await session.RunAsync(
                "MATCH (n) RETURN count(n) AS nodes, count(CASE WHEN n:Entity AND n.owner_id IS NULL THEN 1 END) AS ownerless")).SingleAsync();
            output.WriteLine($"before {arm}: {left["nodes"].As<long>()} nodes in the store, {left["ownerless"].As<long>()} of them ownerless entities");
        }
        var rows = await IdentityKindsBenchmark.RunArmAsync(arm, fixture.Uri, IdentityKindsNeo4jFixture.User, IdentityKindsNeo4jFixture.Password, fixture.Driver);

        using var baseline = JsonDocument.Parse(await File.ReadAllTextAsync(BaselinePath()));
        var expected = baseline.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("arm").GetString() == arm)
            .ToDictionary(r => r.GetProperty("case").GetString()!);
        var tally = baseline.RootElement.GetProperty("byArm").GetProperty(arm);

        output.WriteLine($"{arm}: {rows.Count} cases, right {rows.Count(r => r.Verdict == "right")}, split {rows.Count(r => r.Verdict == "split")}, " +
                         $"merged {rows.Count(r => r.Verdict == "merged")}, failed {rows.Count(r => r.Verdict == "failed")}, " +
                         $"wrong merges {rows.Sum(r => r.WrongMerges)}, open proposals {rows.Sum(r => r.OpenProposals)}");
        foreach (var failed in rows.Where(r => r.Failure is not null))
            output.WriteLine($"  failed {failed.Case}: {failed.Failure}");

        var moved = new List<string>();
        foreach (var row in rows)
        {
            var was = expected[row.Case];
            var before = (was.GetProperty("verdict").GetString(), was.GetProperty("separateThings").GetInt32(), was.GetProperty("wrongMerges").GetInt32(),
                was.GetProperty("lastMentionJoinedToTheRightThing").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null },
                was.GetProperty("openProposals").GetInt32());
            var now = (row.Verdict, row.SeparateThings, row.WrongMerges, row.LastMentionJoinedToTheRightThing, row.OpenProposals);
            if (before != now)
                moved.Add($"{row.Case}: was {before}, now {now} [{string.Join(", ", row.Decisions)}]");
        }
        foreach (var line in moved) output.WriteLine("  moved " + line);
        // Re-recording, only when asked: IKB_TIER1_RECORD=<folder> writes this arm's rows there, in the baseline's shape.
        if (Environment.GetEnvironmentVariable("IKB_TIER1_RECORD") is { Length: > 0 } record)
        {
            Directory.CreateDirectory(record);
            await File.WriteAllTextAsync(Path.Combine(record, arm + ".json"), JsonSerializer.Serialize(rows.Select(r => new
            {
                @case = r.Case, arm = r.Arm, realThings = r.RealThings, separateThings = r.SeparateThings, verdict = r.Verdict,
                wrongMerges = r.WrongMerges, lastMentionJoinedToTheRightThing = r.LastMentionJoinedToTheRightThing, openProposals = r.OpenProposals,
                timesNamedInProfileBlock = r.TimesNamedInProfileBlock, entitiesRecalledForTheName = r.EntitiesRecalledForTheName, decisions = r.Decisions,
            })));
        }

        rows.Select(r => r.Case).Should().BeEquivalentTo(expected.Keys);
        if (arm != "previous")
            rows.Sum(r => r.WrongMerges).Should().Be(0, "evidence-based identity must never merge two real things");
        moved.Should().BeEmpty("each case's verdict, things, merges and open proposals are as recorded; a change that moves a row updates the baseline with its reason");
        rows.Count(r => r.Verdict == "right").Should().Be(tally.GetProperty("right").GetInt32());
        rows.Sum(r => r.OpenProposals).Should().Be(tally.GetProperty("openProposals").GetInt32());
    }
}
