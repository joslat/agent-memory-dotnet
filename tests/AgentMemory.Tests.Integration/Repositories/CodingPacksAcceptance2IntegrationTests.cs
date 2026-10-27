#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.Decisions;
using AgentMemory.Packs.Intent;
using AgentMemory.Packs.Repository;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// CP-acceptance on a real Neo4j, the rest: a repository lesson is its owner's alone while shared decisions are everyone's;
/// the decisions' relevant view leaves a superseded decision out; an anchor that matches nothing reaches nothing; and the
/// intent pack corrects and closes its records as the code map does.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task ALessonIsItsOwnersAloneSharedDecisionsAreEveryonesAndASupersededDecisionLeavesTheRelevantView()
    {
        // An owner-scoped coding record: the repository pack's lesson.
        var lessons = KnowledgePack.ResolutionOf(RepositoryPack.Manifest);
        await new AgentMemory.Extensibility.Neo4j.Migrations.ModuleMigrator(fixture.TransactionRunner, lessons).ApplyAsync("integration");
        await Seed("RepoAccept", "alex");
        var batch = new ModuleWriteBatch
        {
            ModuleId = RepositoryPack.Id, ManifestFingerprint = lessons.ManifestFingerprint!, Provider = new() { Id = RepositoryPack.Id + ":writer" },
            Request = new() { SessionId = "RepoAccept-session", Owner = "alex", Messages = [new() { Id = "RepoAccept-m1", Role = "user", Text = "lesson" }] },
            AtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
            Proposals = [new() { Index = 0, Proposal = new() { Type = RepositoryPack.Type, Key = "build-without-node-reuse",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["kind"] = JsonSerializer.SerializeToElement("pitfall"), ["lesson"] = JsonSerializer.SerializeToElement("Build with node reuse off, or the CLI's DLLs stay locked."),
                    ["repository"] = JsonSerializer.SerializeToElement("acme"),
                },
                Source = new() { MessageIds = ["RepoAccept-m1"], Excerpt = "lesson" } } }],
        };
        (await new Neo4jModuleStore(fixture.TransactionRunner, lessons).WriteAsync(batch)).Single().Status.Should().Be(ProposalWriteStatus.Stored);

        // Shared coding records beside it: decisions, one superseded by the other.
        var root = Directory.CreateTempSubdirectory("decisions-view-it-").FullName;
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(31);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs", "adr"));
            File.WriteAllText(Path.Combine(root, "docs", "adr", "0101-view-cache-per-owner.md"), Adr("1. Cache per owner", "Accepted", "Keep one cache per owner."));
            File.WriteAllText(Path.Combine(root, "docs", "adr", "0102-view-cache-per-store.md"), Adr("2. Cache per store", "Accepted", "Keep one cache per store.", "\n\nSupersedes ADR-0101."));
            await store.ImportAsync(NoDecisions("git:clear"), at.AddSeconds(-1));
            await store.ImportAsync(DecisionsReader.Read(root, ["docs/adr/0101-view-cache-per-owner.md", "docs/adr/0102-view-cache-per-store.md"], CodeIndex.Scan(root, []), "git:views"), at);

            using var services = Policies();
            var policy = services.GetRequiredService<IMemoryIsolationPolicy>();
            var lessonReader = new Neo4jModuleReader(fixture.TransactionRunner, lessons, RepositoryPack.Id, policy);
            var lessonQuery = new ModuleQuery { ModuleId = RepositoryPack.Id, Type = RepositoryPack.Type, Limit = 10 };
            (await lessonReader.FindAsync(lessonQuery, new ModuleReadContext { Scope = MemoryScope.For("alex") })).Records.Should().ContainSingle();
            (await lessonReader.FindAsync(lessonQuery, new ModuleReadContext { Scope = MemoryScope.For("sam") })).Records.Should().BeEmpty("a lesson is its owner's alone");

            var decisions = KnowledgePack.ResolutionOf(DecisionsPack.Manifest);
            var decisionReader = new Neo4jModuleReader(fixture.TransactionRunner, decisions, DecisionsPack.Id, policy);
            var everyone = new ModuleReadContext { Scope = MemoryScope.For("sam") };
            (await decisionReader.FindAsync(new ModuleQuery { ModuleId = DecisionsPack.Id, Type = DecisionsPack.Type, Limit = 10 }, everyone))
                .Records.Should().HaveCount(2, "shared decisions are everyone's");
            var relevant = await new ModuleViews(decisions, decisionReader).EvaluateWithArgumentsAsync(DecisionsPack.RelevantView, new ModuleViewArguments { Search = "cache" }, everyone);
            relevant.Status.Should().Be(ModuleReadStatus.Succeeded);
            relevant.Items.Select(i => i.Record.Key).Should().Equal(["docs/adr/0102-view-cache-per-store.md#decision"], "the relevant view leaves the superseded decision out");

            // An anchor that matches nothing reaches nothing, and says so as an empty result, not a refusal.
            var walk = new Neo4jModuleTraversalReader(fixture.TransactionRunner, decisions, policy);
            var nothing = await walk.MatchAsync(new() { ModuleId = DecisionsPack.Id, Type = DecisionsPack.Type, Property = "appliesTo",
                Match = ModuleAnchorMatch.Glob, Values = ["src/Nowhere/Gone.cs"] }, everyone);
            nothing.Status.Should().Be(ModuleReadStatus.Succeeded);
            nothing.Records.Should().BeEmpty();

            await store.ImportAsync(NoDecisions("git:clear"), at.AddSeconds(1));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TheIntentPackCorrectsARequirementAndClosesOneThatIsGone()
    {
        var root = Directory.CreateTempSubdirectory("intent-it-").FullName;
        var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
        var at = DateTimeOffset.UtcNow.AddMinutes(35);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            void Requirements(params string[] lines) => File.WriteAllText(Path.Combine(root, "docs", "requirements.md"), "# Requirements\n\n" + string.Join("\n", lines) + "\n");
            ModuleSnapshot Read(string source) => IntentReader.Read(root, IntentReader.IntentFiles(["docs/requirements.md"]), source, out _);
            Requirements("- REQ-1: The service shall answer a recall in under 50 ms.", "- REQ-2: When a pack is switched off, the service shall recall nothing from it.");
            var first = Read("git:one");
            first.Records.Should().HaveCountGreaterThanOrEqualTo(2, "the EARS lines are read as requirements");
            await store.ImportAsync(new ModuleSnapshot { Module = IntentPack.Manifest, Source = "git:clear", Records = [] }, at.AddSeconds(-1));
            var created = (await store.ImportAsync(first, at)).Created;

            Requirements("- REQ-1: The service shall answer a recall in under 40 ms.", "- REQ-2: When a pack is switched off, the service shall recall nothing from it.");
            (await store.ImportAsync(Read("git:two"), at.AddSeconds(1))).Should().Be(new ModuleSnapshotResult
                { ModuleId = IntentPack.Id, Source = "git:two", Changed = 1, Unchanged = created - 1 }, "a reworded requirement is a new version");

            Requirements("- REQ-1: The service shall answer a recall in under 40 ms.");
            (await store.ImportAsync(Read("git:three"), at.AddSeconds(2))).Closed.Should().Be(1, "a requirement that is gone is closed, its history kept");

            await store.ImportAsync(new ModuleSnapshot { Module = IntentPack.Manifest, Source = "git:clear", Records = [] }, at.AddSeconds(3));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
