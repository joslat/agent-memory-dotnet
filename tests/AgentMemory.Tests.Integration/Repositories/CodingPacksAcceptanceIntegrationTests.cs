#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// CP-acceptance on a real Neo4j: the decisions pack over its life (a correction is a new version with the old one kept, a
/// later decision supersedes an earlier one by link, a removed ADR is closed, the projection follows the store), and an
/// onboarding round trip (only what a person accepted reaches the store, and it says who accepted it).
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static ModuleSnapshot NoDecisions(string source) => new() { Module = DecisionsPack.Manifest, Source = source, Records = [] };

    private static string Adr(string title, string status, string decision, string extra = "") =>
        $"# {title}\n\n## Status\n\n{status}\n\n## Context\n\nWhy it matters.\n\n## Decision\n\n{decision}{extra}\n";

    [Fact]
    public async Task ADecisionIsCorrectedSupersededAndRemovedWithItsHistoryKeptAndItsProjectionFollows()
    {
        var root = Directory.CreateTempSubdirectory("decisions-it-").FullName;
        try
        {
            var adr = Path.Combine(root, "docs", "adr");
            Directory.CreateDirectory(adr);
            File.WriteAllText(Path.Combine(adr, "0001-cache-per-owner.md"), Adr("1. Cache per owner", "Accepted", "Keep one cache per owner."));
            File.WriteAllText(Path.Combine(adr, "0002-graph-store.md"), Adr("2. Graph store", "Accepted", "Store memory in Neo4j."));
            string[] files = ["docs/adr/0001-cache-per-owner.md", "docs/adr/0002-graph-store.md"];
            ModuleSnapshot Read(string source, params string[] extra) =>
                DecisionsReader.Read(root, [.. files, .. extra], CodeIndex.Scan(root, []), source);
            const string First = "docs/adr/0001-cache-per-owner.md#decision", Third = "docs/adr/0003-cache-per-store.md#decision";

            var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
            var at = DateTimeOffset.UtcNow.AddMinutes(20);
            await store.ImportAsync(NoDecisions("git:clear"), at.AddSeconds(-1));
            (await store.ImportAsync(Read("git:one"), at)).Created.Should().Be(2);

            // A correction: the ADR's decision is reworded. A new version; the first is kept as history.
            File.WriteAllText(Path.Combine(adr, "0001-cache-per-owner.md"), Adr("1. Cache per owner", "Accepted", "Keep one bounded cache per owner."));
            (await store.ImportAsync(Read("git:two"), at.AddSeconds(1))).Should().Be(new ModuleSnapshotResult
                { ModuleId = DecisionsPack.Id, Source = "git:two", Changed = 1, Unchanged = 1 });

            // A later decision supersedes it: linked, and the earlier one reads as superseded though its file still says Accepted.
            File.WriteAllText(Path.Combine(adr, "0003-cache-per-store.md"), Adr("3. Cache per store", "Accepted", "One cache per store.", "\n\nSupersedes ADR-0001."));
            (await store.ImportAsync(Read("git:three", "docs/adr/0003-cache-per-store.md"), at.AddSeconds(2))).Should().Be(new ModuleSnapshotResult
                { ModuleId = DecisionsPack.Id, Source = "git:three", Created = 1, Changed = 1, Unchanged = 1 });
            var link = await Rows("MATCH (n:DecisionsDecision {key:'" + Third + "'})-[:DECISIONS_SUPERSEDES]->(o:DecisionsDecision {key:'" + First + "'}) "
                + "WHERE n.superseded_by IS NULL AND n.invalidated_at IS NULL AND o.superseded_by IS NULL RETURN o.status AS status");
            link.Should().ContainSingle().Which["status"].As<string>().Should().Be("superseded");
            (await Rows("MATCH (d:DecisionsDecision {key:'" + First + "'}) RETURN count(d) AS versions")).Single()["versions"].As<long>()
                .Should().Be(3, "the original, the correction and the superseded version are all kept");

            // The projection follows the store: written once, up to date, stale after a change, never overwritten once edited by hand.
            var projection = ModuleProjection.Render(Read("git:three", "docs/adr/0003-cache-per-store.md"));
            ModuleProjection.Write(root, [projection]).Single().State.Should().Be(ProjectionState.Missing);
            ModuleProjection.Check(root, [projection]).Single().State.Should().Be(ProjectionState.UpToDate);
            File.Delete(Path.Combine(adr, "0002-graph-store.md"));
            (await store.ImportAsync(DecisionsReader.Read(root, ["docs/adr/0001-cache-per-owner.md", "docs/adr/0003-cache-per-store.md"], CodeIndex.Scan(root, []), "git:four"),
                at.AddSeconds(3))).Closed.Should().Be(1, "a removed ADR is closed, its history kept");
            var current = ModuleProjection.Render(DecisionsReader.Read(root, ["docs/adr/0001-cache-per-owner.md", "docs/adr/0003-cache-per-store.md"], CodeIndex.Scan(root, []), "git:four"));
            ModuleProjection.Check(root, [current]).Single().State.Should().Be(ProjectionState.Stale);
            File.AppendAllText(Path.Combine(root, current.Path), "\nA note added by hand.\n");
            ModuleProjection.Write(root, [current]).Single().State.Should().Be(ProjectionState.Edited);
            File.ReadAllText(Path.Combine(root, current.Path)).Should().Contain("A note added by hand.");

            await store.ImportAsync(NoDecisions("git:clear"), at.AddSeconds(4));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OnlyAnAcceptedProposalReachesTheStoreAndItSaysWhoAcceptedIt()
    {
        static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        ReviewItem Requirement(string key) => new()
        {
            Type = IntentPack.RequirementType, Key = key,
            Properties = new Dictionary<string, JsonElement>
            {
                ["text"] = J($"The service shall keep {key}."), ["pattern"] = J("ubiquitous"), ["reference"] = J("README.md:3"),
                ["status"] = J("proposed"), ["file"] = J("README.md"), ["source"] = J("README.md:3"),
            },
            Evidence = [new ReviewEvidence("README.md", 3, 3, "The service shall keep it.")], Confidence = 0.9, ProposedBy = "model:test", ProposedAt = DateTimeOffset.UtcNow,
        };
        var set = new ReviewSet { Module = IntentPack.Id, Items = [Requirement("accepted-one"), Requirement("pending-one"), Requirement("rejected-one")] };
        set = ModuleReview.Decide(set, ["accepted-one"], accept: true, "jose", DateTimeOffset.UtcNow);
        set = ModuleReview.Decide(set, ["rejected-one"], accept: false, "jose", DateTimeOffset.UtcNow, "a guess");
        var deterministic = new ModuleSnapshot { Module = IntentPack.Manifest, Source = "git:onboard", Records = [] };

        var merged = ModuleReview.Merge(deterministic, set, out var conflicts);
        conflicts.Should().BeEmpty();
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(25);
        (await store.ImportAsync(merged, at)).Created.Should().Be(1);

        var rows = await Rows("MATCH (r:IntentRequirement) WHERE r.superseded_by IS NULL AND r.invalidated_at IS NULL RETURN r.key AS key, r.source AS source");
        rows.Should().ContainSingle();
        rows.Single()["key"].As<string>().Should().Be("accepted-one");
        rows.Single()["source"].As<string>().Should().StartWith("README.md:3 (accepted by jose on").And.Contain("proposed by model:test");

        await store.ImportAsync(deterministic with { Source = "git:clear" }, at.AddSeconds(1));
    }
}
