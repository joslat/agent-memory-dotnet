extern alias McpHost;

using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using McpHost::AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#pragma warning disable AMWRITE001 // Deterministic writer, no model endpoint, over the real persistence pipeline.

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class StoreIsolationIntegrationTests
{
    [Fact]
    public async Task Coding_worker_persists_sources_and_facts_only_in_the_bound_project_and_forgets_non_destructively()
    {
        var neo4j = new Neo4jOptions
        {
            Uri = _container!.GetConnectionString(), Username = Username, Password = Password,
            Database = "neo4j", EmbeddingDimensions = Dimensions,
        };
        // Test setup alone provisions the two databases. The service itself cannot create one.
        var provision = new Neo4jMemoryStoreProvisioner(_sessionFactory, Options.Create(neo4j),
            Options.Create(new MemoryStoreOptions
            {
                Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = true,
            }), NullLogger<Neo4jMemoryStoreProvisioner>.Instance);
        await provision.EnsureStoreAsync("alpha");
        await provision.EnsureStoreAsync("beta");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(o =>
        {
            o.Uri = neo4j.Uri; o.Username = neo4j.Username; o.Password = neo4j.Password;
            o.Database = neo4j.Database; o.EmbeddingDimensions = Dimensions;
        }, store =>
        {
            store.Strategy = MemoryStorageStrategy.DatabasePerApplication;
            store.DatabasePrefix = "coding-"; store.AutoProvision = false;
        });
        services.AddAgentMemoryCore(memory =>
        {
            memory.Extraction.FallBackToExtractorsWhenWriterFails = false;
            memory.Extraction.FailureMode = IngestionFailureMode.FailFast;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new StubEmbeddingGenerator(
            NullLogger<StubEmbeddingGenerator>.Instance, Dimensions));
        var model = new CodingTestWriter();
        services.AddSingleton<IMemoryWriter>(model);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var scopes = new CodingOperationScope(provider.GetRequiredService<IServiceScopeFactory>());
        var operations = new CodingMemoryOperations(scopes, provider.GetRequiredService<IOptions<MemoryOptions>>());
        var writer = new CodingWriter(scopes);
        var alpha = new CodingIdentity("owner", "alpha");
        var beta = new CodingIdentity("owner", "beta");
        foreach (var identity in new[] { alpha, beta })
            await scopes.RunAsync(identity, async (sp, ct) =>
            {
                await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(ct);
                return true;
            });

        var request = new CodingRememberRequest
        {
            Session = "same-client-session", TurnId = "same-turn", UserText = "alpha-choice",
            Commit = new string('a', 40), Source = "settings.cs",
        };
        using var queue = new CodingWriteQueue(writer);
        await queue.StartAsync(default);
        CodingWriteJob alphaJob;
        try
        {
            var first = queue.TryEnqueue(alpha, request)!;
            var second = queue.TryEnqueue(beta, request with { UserText = "beta-choice" })!;
            alphaJob = await AwaitCodingJobAsync(queue, alpha, first.JobId);
            alphaJob.State.Should().Be(CodingJobState.Succeeded, alphaJob.ErrorCode);
            var betaJob = await AwaitCodingJobAsync(queue, beta, second.JobId);
            betaJob.State.Should().Be(CodingJobState.Succeeded, betaJob.ErrorCode);
            queue.Get(beta, first.JobId).Should().BeNull();
        }
        finally { await queue.StopAsync(default); }

        var alphaFact = (await operations.HistoryAsync(alpha)).Should().ContainSingle().Subject;
        var betaFact = (await operations.HistoryAsync(beta)).Should().ContainSingle().Subject;
        alphaFact.Summary.Should().Contain("alpha-choice").And.NotContain("beta-choice");
        betaFact.Summary.Should().Contain("beta-choice").And.NotContain("alpha-choice");
        alphaFact.OwnerId.Should().Be("owner");
        alphaFact.SourceMessageIds.Should().Contain(alphaJob.SourceMessageId!);
        var source = await scopes.RunAsync(alpha, (sp, ct) =>
            sp.GetRequiredService<IMessageRepository>().GetByIdAsync(alphaJob.SourceMessageId!, ct));
        source!.Metadata["coding_commit"].ToString().Should().Be(request.Commit);
        source.Metadata["coding_source"].ToString().Should().Be(request.Source);
        (await scopes.RunAsync(beta, (sp, ct) =>
            sp.GetRequiredService<IMessageRepository>().GetByIdAsync(alphaJob.SourceMessageId!, ct))).Should().BeNull();

        // A new queue/process cannot infer completion from the source alone; no second writer call.
        (await writer.WriteAsync(alpha, request)).PriorAttemptUnknown.Should().BeTrue();
        model.Calls.Should().Be(2);
        (await operations.ForgetAsync(beta, MemoryHistoryKind.Fact, alphaFact.Id)).Should().BeFalse();
        (await operations.ForgetAsync(alpha, MemoryHistoryKind.Fact, alphaFact.Id)).Should().BeTrue();
        (await operations.HistoryAsync(alpha, alphaFact.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(MemoryHistoryStatus.Invalidated);
        (await operations.HistoryAsync(beta, alphaFact.Id)).Should().BeEmpty();
        (await operations.HistoryAsync(beta, betaFact.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(MemoryHistoryStatus.Live);
    }

    private static async Task<CodingWriteJob> AwaitCodingJobAsync(CodingWriteQueue queue, CodingIdentity identity, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var job = queue.Get(identity, id)!;
            if (job.State is not (CodingJobState.Queued or CodingJobState.Running)) return job;
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class CodingTestWriter : IMemoryWriter
    {
        public bool IsEnabled => true;
        public int Calls { get; private set; }
        public Task<UnifiedExtractionResult> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new UnifiedExtractionResult
            {
                Facts = [new ExtractedFact { Subject = "Project", Predicate = "uses", Object = request.Window.Targets.Single(m => m.Role == "user").Content }],
            });
        }
    }
}
