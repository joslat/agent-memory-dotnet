using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Neo4j.Driver;

#pragma warning disable AMWRITE001, AMGATE001 // Fixed writer/judge proposals isolate persistence from model variance.

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// R2: ExtractionRequest.OperationId applies a store-aware writer's turn once, completely or not at all, with its receipt
/// on the stored message in the same transaction as its effects.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class AppliedOnceIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    private const string Owner = "applied-once-owner";

    public Task InitializeAsync() => fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Harness(ServiceProvider Provider, IServiceScope Scope, IMemoryWriter Writer, IMemoryUpdateJudge Judge)
        : IAsyncDisposable
    {
        public T Get<T>() where T : notnull => Scope.ServiceProvider.GetRequiredService<T>();

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose();
            await Provider.DisposeAsync();
        }
    }

    private Harness Build(UnifiedExtractionResult proposal, double judgeScore = 0.95, bool? identity = false, bool fallBack = false)
    {
        var writer = Substitute.For<IMemoryWriter>();
        writer.IsEnabled.Returns(true);
        writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>()).Returns(proposal);
        var judge = Substitute.For<IMemoryUpdateJudge>();
        judge.IsEnabled.Returns(true);
        judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<string, double>)c.Arg<MemoryUpdateRequest>().Pairs.ToDictionary(p => p.Key, _ => judgeScore));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(writer);
        services.AddSingleton(judge);
        services.Configure<IdentityResolutionOptions>(o => o.Enabled = identity);
        services.AddNeo4jAgentMemory(new MemoryOptions
        {
            ConfidenceReinforcementAlpha = 0.05,
            Extraction = new() { FallBackToExtractorsWhenWriterFails = fallBack },
        }, n =>
        {
            n.Uri = fixture.ConnectionString; n.Username = fixture.User; n.Password = fixture.Password;
            n.Database = "neo4j"; n.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        var provider = services.BuildServiceProvider(validateScopes: true);
        return new Harness(provider, provider.CreateScope(), writer, judge);
    }

    private static async Task<Message> StoredMessage(Harness harness, string id, string text, bool store = true)
    {
        var message = new Message
        {
            MessageId = id, ConversationId = "applied-once", SessionId = "applied-once", Role = "user", Content = text,
            TimestampUtc = DateTimeOffset.UtcNow,
        };
        if (!store) return message;
        var shortTerm = harness.Get<IShortTermMemoryService>();
        await shortTerm.AddConversationAsync("applied-once", "applied-once", userId: Owner);
        return await shortTerm.AddMessageAsync(message);
    }

    private static ExtractionRequest Request(Message message, string operationId) => new()
    {
        InputMode = ExtractionInputMode.ConversationTurn, SessionId = "applied-once", UserId = Owner,
        Messages = [message], OperationId = operationId,
    };

    private Task<T> Scalar<T>(string query, object parameters) => fixture.TransactionRunner.ReadAsync(async runner =>
    {
        var cursor = await runner.RunAsync(query, parameters);
        return ValueExtensions.As<T>((await cursor.SingleAsync())[0]);
    });

    [Fact]
    public async Task The_receipt_commits_with_the_effects_and_asking_again_runs_nothing()
    {
        await using var harness = Build(new UnifiedExtractionResult
        {
            Facts = [new() { Subject = "user", Predicate = "plays", Object = "the oboe" }],
            Preferences = [new() { Category = "general", PreferenceText = "Prefers mornings" }],
        });
        var message = await StoredMessage(harness, "oboe", "I play the oboe and I prefer mornings.");
        var pipeline = harness.Get<IMemoryExtractionPipeline>();

        var first = await pipeline.ExtractAsync(Request(message, "op-oboe"));

        first.AppliedBefore.Should().BeFalse();
        first.Receipt.Should().NotBeNull();
        first.Receipt!.FactIds.Should().ContainSingle();
        first.Receipt.PreferenceIds.Should().ContainSingle();
        (await Scalar<string>("MATCH (m:Message {id: $id}) RETURN m.applied_operation_id", new { id = "oboe" })).Should().Be("op-oboe");
        var factId = first.Receipt.FactIds[0];

        var again = await pipeline.ExtractAsync(Request(message, "op-oboe"));

        again.AppliedBefore.Should().BeTrue();
        again.Receipt!.FactIds.Should().Equal(first.Receipt.FactIds);
        await harness.Writer.Received(1).WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>());
        (await Scalar<long>("MATCH (f:Fact {id: $id}) RETURN f.mention_count", new { id = factId })).Should().Be(1);
        (await Scalar<long>("MATCH (f:Fact) WHERE f.owner_id = $owner RETURN count(f)", new { owner = Owner })).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_inside_the_transaction_keeps_neither_the_memories_nor_a_receipt()
    {
        await using var harness = Build(new UnifiedExtractionResult
        {
            Facts = [new() { Subject = "user", Predicate = "keeps", Object = "bees on the roof" }],
        });
        // The message is never stored: the receipt cannot be written, so the transaction must roll everything back.
        var message = await StoredMessage(harness, "bees", "I keep bees on the roof.", store: false);

        var act = () => harness.Get<IMemoryExtractionPipeline>().ExtractAsync(Request(message, "op-bees"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is not stored*");
        (await Scalar<long>("MATCH (f:Fact) WHERE f.owner_id = $owner RETURN count(f)", new { owner = Owner })).Should().Be(0);
    }

    [Fact]
    public async Task The_judge_is_asked_before_the_write_and_its_verdict_closes_the_named_memory()
    {
        await using var harness = Build(new UnifiedExtractionResult
        {
            Facts = [new() { Subject = "user", Predicate = "works in", Object = "Basel", ReplacesId = "old-work" }],
        });
        await harness.Get<IFactRepository>().UpsertAsync(new Fact
        {
            FactId = "old-work", Subject = "user", Predicate = "works in", Object = "Zurich", OwnerId = Owner,
            Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var message = await StoredMessage(harness, "basel", "I work in Basel now.");

        var result = await harness.Get<IMemoryExtractionPipeline>().ExtractAsync(Request(message, "op-basel"));

        await harness.Judge.Received(1).JudgeAsync(Arg.Is<MemoryUpdateRequest>(r => r.Named && r.Pairs.Count == 1), Arg.Any<CancellationToken>());
        result.Receipt!.ClosedIds.Should().Equal("old-work");
        ((await harness.Get<IFactRepository>().GetByIdAsync("old-work"))!.InvalidatedAtUtc is not null).Should().BeTrue();
    }

    [Fact]
    public async Task Another_operations_receipt_is_refused_and_the_same_operations_is_returned()
    {
        await using var harness = Build(new UnifiedExtractionResult
        {
            Facts = [new() { Subject = "user", Predicate = "reads", Object = "poetry" }],
        });
        var message = await StoredMessage(harness, "poetry", "I read poetry.");
        await fixture.TransactionRunner.WriteAsync(async runner => await runner.RunAsync(
            "MATCH (m:Message {id: 'poetry'}) SET m.applied_operation_id = 'op-other', m.applied_at_utc = '2026-11-20T10:00:00.0000000+00:00', m.applied_fact_ids = [], m.applied_preference_ids = [], m.applied_closed_ids = []"));
        var pipeline = harness.Get<IMemoryExtractionPipeline>();

        var other = () => pipeline.ExtractAsync(Request(message, "op-poetry"));
        await other.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already applied by operation 'op-other'*");
        (await pipeline.ExtractAsync(Request(message, "op-other"))).AppliedBefore.Should().BeTrue();
        await harness.Writer.DidNotReceive().WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_confirmation_counts_once_however_often_the_operation_is_asked()
    {
        await using var harness = Build(new UnifiedExtractionResult { ConfirmedFactIds = ["said-before"] });
        await harness.Get<IFactRepository>().UpsertAsync(new Fact
        {
            FactId = "said-before", Subject = "user", Predicate = "lives in", Object = "Bern", OwnerId = Owner,
            Confidence = 0.8, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });
        var before = await Scalar<long>("MATCH (f:Fact {id: 'said-before'}) RETURN coalesce(f.mention_count, 1)", new { });
        var message = await StoredMessage(harness, "bern", "I live in Bern, as I said.");
        var pipeline = harness.Get<IMemoryExtractionPipeline>();

        await pipeline.ExtractAsync(Request(message, "op-bern"));
        await pipeline.ExtractAsync(Request(message, "op-bern"));

        (await Scalar<long>("MATCH (f:Fact {id: 'said-before'}) RETURN f.mention_count", new { })).Should().Be(before + 1);
    }

    [Fact]
    public async Task Options_whose_effects_one_transaction_cannot_cover_are_refused()
    {
        await using var identityOn = Build(new UnifiedExtractionResult(), identity: true);
        var message = await StoredMessage(identityOn, "identity", "Hello.");
        var identity = () => identityOn.Get<IMemoryExtractionPipeline>().ExtractAsync(Request(message, "op-identity"));
        await identity.Should().ThrowAsync<NotSupportedException>().WithMessage("*evidence-based identity off*");

        await using var fallBackWithJudge = Build(new UnifiedExtractionResult(), fallBack: true);
        var fallBack = () => fallBackWithJudge.Get<IMemoryExtractionPipeline>().ExtractAsync(Request(message, "op-fallback"));
        await fallBack.Should().ThrowAsync<NotSupportedException>().WithMessage("*FallBackToExtractorsWhenWriterFails off*");
    }
}
