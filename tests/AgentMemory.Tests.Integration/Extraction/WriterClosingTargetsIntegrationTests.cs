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

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class WriterClosingTargetsIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(0.95, true)]
    [InlineData(0.10, false)]
    public async Task One_persisted_replacement_keeps_both_closing_proposals_and_obeys_the_judge(double score, bool closes)
    {
        const string owner = "writer-targets";
        var writer = Substitute.For<IMemoryWriter>();
        writer.IsEnabled.Returns(true);
        writer.WriteAsync(Arg.Any<MemoryWriteRequest>(), Arg.Any<CancellationToken>()).Returns(new UnifiedExtractionResult
        {
            Facts =
            [
                new() { Subject = "Alice", Predicate = "lives in", Object = "Lisbon", ReplacesId = "old-home" },
                new() { Subject = "Alice", Predicate = "lives in", Object = "Lisbon", ReplacesId = "old-plan" },
            ],
        });
        var judge = Substitute.For<IMemoryUpdateJudge>();
        judge.IsEnabled.Returns(true);
        judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => (IReadOnlyDictionary<string, double>)c.Arg<MemoryUpdateRequest>().Pairs.ToDictionary(p => p.Key, _ => score));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(writer);
        services.AddSingleton(judge);
        services.AddNeo4jAgentMemory(new MemoryOptions { Extraction = new() { DeduplicateWithinExtraction = true } }, n =>
        {
            n.Uri = fixture.ConnectionString; n.Username = fixture.User; n.Password = fixture.Password;
            n.Database = "neo4j"; n.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
        foreach (var (id, predicate, value) in new[] { ("old-home", "lives in", "Porto"), ("old-plan", "plans to move to", "Lisbon") })
            await facts.UpsertAsync(new Fact
            {
                FactId = id, Subject = "Alice", Predicate = predicate, Object = value,
                OwnerId = owner, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            });
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        await shortTerm.AddConversationAsync("writer-targets", "writer-targets", userId: owner);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = "move", ConversationId = "writer-targets", SessionId = "writer-targets", Role = "user",
            Content = "I moved to Lisbon.", TimestampUtc = DateTimeOffset.UtcNow,
        });

        await scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>().ExtractAsync(new ExtractionRequest
        {
            SessionId = "writer-targets", UserId = owner, Messages = [message],
        });

        await judge.Received(1).JudgeAsync(Arg.Is<MemoryUpdateRequest>(r => r.Named && r.Pairs.Count == 2), Arg.Any<CancellationToken>());
        foreach (var id in new[] { "old-home", "old-plan" })
            ((await facts.GetByIdAsync(id))!.InvalidatedAtUtc is not null).Should().Be(closes);
        var live = (await facts.GetBySubjectAsync("Alice", MemoryScope.For(owner, includeShared: false)))
            .Where(f => f.InvalidatedAtUtc is null).ToList();
        live.Should().HaveCount(closes ? 1 : 3);
        var replacement = live.Should().ContainSingle(f => f.Predicate == "lives in" && f.Object == "Lisbon").Which;
        var mentions = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (f:Fact {id: $id}) RETURN f.mention_count AS mentions", new { id = replacement.FactId });
            return global::Neo4j.Driver.ValueExtensions.As<int>((await cursor.SingleAsync())["mentions"]);
        });
        mentions.Should().Be(1, "one telling with two closing targets is still one mention");
    }
}
