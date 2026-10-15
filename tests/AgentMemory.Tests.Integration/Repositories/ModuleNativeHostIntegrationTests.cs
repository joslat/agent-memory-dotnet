#pragma warning disable AMEXT001, MAAI001
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class ModuleRecordWriteIntegrationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NativeHostStoresSourcesAndRecallsModuleViewInFreshSession(bool background, bool ownedConversation)
    {
        var prefix = !ownedConversation ? "HostUnowned" : background ? "HostBackground" : "HostInline";
        var module = Resolve(prefix).Modules.Single();
        module = module with
        {
            Ontology = module.Ontology! with { Types = [module.Ontology.Types.Single() with { Render = "Remember {text}" }] },
            Views = [new() { Id = "all", Type = prefix + "Item", Limit = 10 }]
        };
        var resolution = ModuleResolver.Resolve([module], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        resolution.Succeeded.Should().BeTrue();
        await new ModuleMigrator(fixture.TransactionRunner, resolution).ApplyAsync("native-host-integration");
        var captured = new List<IngestionRequest>(); var reports = new List<IngestionReport>();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddNeo4jAgentMemory(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant, o =>
        {
            o.Uri = fixture.ConnectionString; o.Username = fixture.User; o.Password = fixture.Password;
            o.Database = "neo4j"; o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => new StubEmbeddingGenerator(
            sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        services.AddAgentMemoryFramework(o => { o.AutoExtractOnPersist = false; o.ExtractInBackground = background; });
        services.AddAgentMemoryFrameworkModuleIngestion(sp => new CapturingHostIngestion(new ModuleIngestion(resolution,
            [new HostProvider(module.Id, prefix + "Item", captured)],
            new Dictionary<string, IModuleStore> { [module.Id] = new Neo4jModuleStore(sp.GetRequiredService<INeo4jTransactionRunner>(), resolution) },
            sp.GetRequiredService<IMemoryIsolationPolicy>(), TimeProvider.System, sp.GetRequiredService<ILogger<ModuleIngestion>>()), reports));
        services.AddScoped<IContextContributor>(sp => new ViewContributor(resolution,
            new Neo4jModuleReader(sp.GetRequiredService<INeo4jTransactionRunner>(), resolution, module.Id, sp.GetRequiredService<IMemoryIsolationPolicy>()), "all"));
        await using var root = services.BuildServiceProvider(validateScopes: true);
        var queue = root.GetRequiredService<IBackgroundExtraction>();
        if (ownedConversation)
        {
            using var setup = root.CreateScope();
            await setup.ServiceProvider.GetRequiredService<IShortTermMemoryService>().AddConversationAsync(
                prefix + "-conversation", prefix + "-session", "owner");
        }
        // Each turn's originating scope is gone before queued work is explicitly drained.
        foreach (var marker in new[] { "first lesson", "corrected lesson" })
        {
            using var scope = root.CreateScope();
            var session = Substitute.For<AgentSession>(); session.WithMemoryIdentity("owner", prefix + "-session", prefix + "-conversation");
            var provider = scope.ServiceProvider.GetRequiredService<Neo4jMemoryContextProvider>();
            var agent = Substitute.For<AIAgent>();
            await provider.InvokingAsync(new AIContextProvider.InvokingContext(agent, session,
                new AIContext { Messages = [new ChatMessage(ChatRole.User, marker)] }), CancellationToken.None);
            await provider.InvokedAsync(new AIContextProvider.InvokedContext(Substitute.For<AIAgent>(), session,
                [new ChatMessage(ChatRole.User, marker)], [new ChatMessage(ChatRole.Assistant, "Acknowledged.")]), CancellationToken.None);
        }
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        captured.Should().HaveCount(2);
        captured.Select(r => r.Messages.First().Text).Should().Equal("first lesson", "corrected lesson");
        captured.Should().OnlyContain(r => r.Messages.Count == 2 && r.Messages[0].Role == "user" && r.Messages[1].Role == "assistant");
        var rows = await Rows($"MATCH (n:{prefix}Item) RETURN properties(n) AS record ORDER BY n.version");
        reports.Should().HaveCount(2);
        if (!ownedConversation)
        {
            rows.Should().BeEmpty();
            reports.SelectMany(r => r.Providers).Should().OnlyContain(r => r.Status == ProviderIngestionStatus.Refused);
            reports.SelectMany(r => r.Providers).SelectMany(r => r.Proposals).SelectMany(r => r.Errors)
                .Should().OnlyContain(e => e.Contains("Source message is absent"));
            return;
        }
        reports.SelectMany(r => r.Providers).Should().OnlyContain(r => r.Status == ProviderIngestionStatus.Succeeded,
            JsonSerializer.Serialize(reports));
        rows.Should().HaveCount(2);
        (await Rows($"MATCH (n:{prefix}Item)-[:SUPERSEDED_BY]->(next:{prefix}Item) RETURN count(*) AS n")).Single()["n"].Should().Be(1L);
        var messageRows = await fixture.TransactionRunner.ReadAsync(async runner =>
            await (await runner.RunAsync("MATCH (c:Conversation {id:$conversation})-[:HAS_MESSAGE]->(m:Message) RETURN m.id AS id", new { conversation = prefix + "-conversation" })).ToListAsync());
        captured.SelectMany(r => r.Messages).Select(m => m.Id).Should().BeEquivalentTo(messageRows.Select(r => (string)r["id"]));

        using var readScope = root.CreateScope();
        var reader = new Neo4jModuleReader(readScope.ServiceProvider.GetRequiredService<INeo4jTransactionRunner>(), resolution, module.Id,
            readScope.ServiceProvider.GetRequiredService<IMemoryIsolationPolicy>());
        var current = (await reader.FindAsync(ReadQuery(resolution), ReadContext())).Records.Single();
        current.Source.MessageIds.Should().Equal(captured[1].Messages.Select(m => m.Id));
        current.Properties["text"].GetString().Should().Be("corrected lesson");
        var recalled = await Recall("owner");
        recalled.Instructions.Should().Contain("Remember corrected lesson").And.NotContain("Remember first lesson");
        var foreign = await Recall("foreign");
        (foreign.Instructions ?? "").Should().NotContain("corrected lesson").And.NotContain("first lesson");

        async Task<AIContext> Recall(string owner)
        {
            using var scope = root.CreateScope();
            var session = Substitute.For<AgentSession>(); session.WithMemoryIdentity(owner, prefix + "-fresh-" + owner, prefix + "-fresh-conversation-" + owner);
            return await scope.ServiceProvider.GetRequiredService<Neo4jMemoryContextProvider>().InvokingAsync(
                new AIContextProvider.InvokingContext(Substitute.For<AIAgent>(), session,
                    new AIContext { Messages = [new ChatMessage(ChatRole.User, "What should I remember?")] }), CancellationToken.None);
        }
    }

    private sealed class CapturingHostIngestion(IModuleIngestion inner, List<IngestionReport> reports) : IModuleIngestion
    {
        public async Task<IngestionReport> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        {
            var report = await inner.IngestAsync(request, cancellationToken); reports.Add(report); return report;
        }
        public Task<IngestionReport> ReplayAsync(IngestionRequest request, IReadOnlyList<RecordedProposals> recorded, CancellationToken cancellationToken = default)
            => inner.ReplayAsync(request, recorded, cancellationToken);
    }

    private sealed class HostProvider(string moduleId, string type, List<IngestionRequest> captured) : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor { get; } = new() { Id = moduleId + ":writer" };
        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        {
            captured.Add(request);
            return Task.FromResult<IReadOnlyList<ModuleProposal>>([new()
            {
                Type = type, Key = "lesson", Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(request.Messages[0].Text) },
                Source = new() { MessageIds = request.Messages.Select(m => m.Id).ToArray(), Excerpt = request.Messages[0].Text }
            }]);
        }
    }
}
