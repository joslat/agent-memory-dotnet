#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// EXT-write (design 0.12, W3) on a real Neo4j: a provider sees its existing record, names it as replaced, and an approved
/// replacement stores the new record and invalidates the old one in one transaction, or neither.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Fact]
    public async Task AnApprovedReplacementStoresTheNewRecordAndClosesTheOldOneTogether()
    {
        var resolution = await ReadyWithView("StoreReplace");
        using var services = Policies();
        var reader = Reader(resolution, services);
        var provider = new LessonProvider(resolution);
        var engine = Replacing(resolution, provider, fixture.TransactionRunner, services);

        provider.Next = [provider.Lesson("sleep-loop", "Wrap flaky calls in a sleep loop")];
        (await engine.IngestAsync(provider.Request())).Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Stored);

        provider.Next = [provider.Lesson("retry-helper", "Use the retry helper for flaky calls", "sleep-loop")];
        var report = await engine.IngestAsync(provider.Request());

        provider.Seen!.Existing.Select(e => e.Record.Key).Should().Equal("sleep-loop");
        var outcome = report.Providers.Single().Proposals.Single();
        outcome.Status.Should().Be(ProposalWriteStatus.Stored);
        outcome.Closed.Should().Equal("sleep-loop");
        var current = (await reader.FindAsync(ReadQuery(resolution), ReadContext())).Records;
        current.Select(r => r.Key).Should().Equal("retry-helper");
        var retained = await Rows("MATCH (n:StoreReplaceItem) WHERE n.key = 'sleep-loop' RETURN n.invalidated_at IS NOT NULL AS closed");
        retained.Should().ContainSingle("the replaced record is closed, never deleted").Which["closed"].As<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task AReplacementWhoseTransactionFailsLeavesTheOldRecordCurrent()
    {
        var resolution = await ReadyWithView("StoreReplaceAtomic");
        using var services = Policies();
        var reader = Reader(resolution, services);
        var provider = new LessonProvider(resolution);
        provider.Next = [provider.Lesson("sleep-loop", "Wrap flaky calls in a sleep loop")];
        await Replacing(resolution, provider, fixture.TransactionRunner, services).IngestAsync(provider.Request());

        provider.Next = [provider.Lesson("retry-helper", "Use the retry helper for flaky calls", "sleep-loop")];
        var failed = await Replacing(resolution, provider, new FailingCommit(fixture.TransactionRunner), services).IngestAsync(provider.Request());

        failed.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Unavailable);
        (await reader.FindAsync(ReadQuery(resolution), ReadContext())).Records.Select(r => r.Key).Should().Equal("sleep-loop");
    }

    private ModuleIngestion Replacing(ModuleResolution resolution, LessonProvider provider, INeo4jTransactionRunner runner,
        ServiceProvider services) =>
        new(resolution, [provider], new Dictionary<string, IModuleStore> { [resolution.Modules.Single().Id] = new Neo4jModuleStore(runner, resolution) },
            services.GetRequiredService<IMemoryIsolationPolicy>(), TimeProvider.System, NullLogger<ModuleIngestion>.Instance,
            new ModuleIngestionServices { Views = _ => new ModuleViews(resolution, Reader(resolution, services)), Closings = new Approving() });

    private async Task<ModuleResolution> ReadyWithView(string prefix)
    {
        var manifest = Resolve(prefix).Modules.Single() with { Views = [new() { Id = "current", Type = prefix + "Item", Limit = 20 }] };
        var resolution = ModuleResolver.Resolve([manifest], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        resolution.Succeeded.Should().BeTrue(string.Join("; ", resolution.Errors));
        await Seed(prefix, "owner");
        await new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("integration");
        return resolution;
    }

    private sealed class Approving : IMemoryClosingApprover
    {
        public bool CanApprove(bool named) => true;
        public Task<IReadOnlyDictionary<string, MemoryClosingVerdict>> ApproveAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, MemoryClosingVerdict>>(request.Pairs.ToDictionary(p => p.Key, _ => new MemoryClosingVerdict(0.95, true)));
    }

    private sealed class LessonProvider(ModuleResolution resolution) : IIngestionProvider
    {
        private readonly string _prefix = resolution.Modules.Single().Ontology!.LabelPrefix;
        public IngestionProviderDescriptor Descriptor { get; } = new() { Id = resolution.Modules.Single().Id + ":writer", ExistingView = "current" };
        public IReadOnlyList<ModuleProposal> Next { get; set; } = [];
        public IngestionRequest? Seen { get; private set; }

        public IngestionRequest Request() => new()
        {
            SessionId = _prefix + "-session", Owner = "owner", AtUtc = DateTimeOffset.UtcNow,
            Messages = [new() { Id = _prefix + "-m1", Role = "user", Text = "lesson" }],
        };

        public ModuleProposal Lesson(string key, string text, params string[] replaces) => new()
        {
            Type = _prefix + "Item", Key = key, Replaces = replaces,
            Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(text) },
            Source = new() { MessageIds = [_prefix + "-m1"], Excerpt = "lesson" },
        };

        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            return Task.FromResult(Next);
        }
    }
}
