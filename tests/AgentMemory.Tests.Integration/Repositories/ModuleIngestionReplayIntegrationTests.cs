#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task RecordedIngestionReconstructsActualVersionedGraphWithoutProvidersOrCurrentClock()
    {
        var resolution = await Ready("StoreReplay"); var batch = Batch(resolution); var provider = new ReplayProvider(batch);
        var clock = new ReplayClock(batch.AtUtc); using var services = Policies();
        var engine = new ModuleIngestion(resolution, [provider], new Dictionary<string, IModuleStore>
            { [batch.ModuleId] = new Neo4jModuleStore(fixture.TransactionRunner, resolution) },
            services.GetRequiredService<IMemoryIsolationPolicy>(), clock, NullLogger<ModuleIngestion>.Instance);
        var first = await engine.IngestAsync(batch.Request);
        first.Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        provider.Text = "corrected lesson";
        var second = await engine.IngestAsync(batch.Request);
        second.Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        var before = await ReplayGraph();
        provider.ForbidCalls = true; clock.ForbidCalls = true;

        // Only this isolated fixture's module records/keys are reset. Persisted sources and migration
        // ownership remain as the replay baseline; this is not a product erase/prune path.
        await Execute("MATCH (n:StoreReplayItem) DETACH DELETE n");
        await Execute("MATCH (n:ModuleKey {module_id:'com.example.storereplay'}) DETACH DELETE n");
        (await engine.ReplayAsync(batch.Request, first.Recorded)).Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await engine.ReplayAsync(batch.Request, second.Recorded)).Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Stored);
        (await ReplayGraph()).Should().BeEquivalentTo(before);
        (await engine.ReplayAsync(batch.Request, second.Recorded)).Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.NoOp);
        (await ReplayGraph()).Should().BeEquivalentTo(before);
        provider.Calls.Should().Be(2); clock.Calls.Should().Be(2);

        var reader = Reader(resolution, services);
        var current = (await reader.FindAsync(ReadQuery(resolution), ReadContext())).Records.Single();
        current.Properties["text"].GetString().Should().Be("corrected lesson");
        current.Source.MessageIds.Should().Equal("StoreReplay-m1");
        var historic = (await reader.FindAsync(ReadQuery(resolution), ReadContext() with { SystemAsOfUtc = first.Recorded.Single().AtUtc })).Records.Single();
        historic.Properties["text"].GetString().Should().Be("lesson");
        var wrongOwner = () => engine.ReplayAsync(batch.Request with { Owner = "foreign" }, first.Recorded);
        await wrongOwner.Should().ThrowAsync<ArgumentException>();
        (await ReplayGraph()).Should().BeEquivalentTo(before);
    }

    private async Task<object[]> ReplayGraph()
    {
        var rows = await Rows("MATCH (n:StoreReplayItem) OPTIONAL MATCH (n)-[:SUPERSEDED_BY]->(next:StoreReplayItem) RETURN properties(n) AS row, next.id AS next ORDER BY n.id");
        return rows.Select(r => (object)new { Row = r["row"], Next = r["next"] }).ToArray();
    }

    private sealed class ReplayProvider(ModuleWriteBatch batch) : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor => batch.Provider;
        public string Text { get; set; } = "lesson";
        public bool ForbidCalls { get; set; }
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        {
            if (ForbidCalls) throw new InvalidOperationException("Replay invoked a provider.");
            Calls++;
            return Task.FromResult<IReadOnlyList<ModuleProposal>>([batch.Proposals[0].Proposal with
            { Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(Text) } }]);
        }
    }

    private sealed class ReplayClock(DateTimeOffset atUtc) : TimeProvider
    {
        public int Calls { get; private set; }
        public bool ForbidCalls { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (ForbidCalls) throw new InvalidOperationException("Replay read the current clock.");
            return atUtc.AddSeconds(Calls++);
        }
    }
}
