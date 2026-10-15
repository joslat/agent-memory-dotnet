extern alias RouterArena;

using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using RouterArena::AgentMemory.RouterArena.Record;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;

#pragma warning disable AMGATE001 // Exercise the recorder's real pre-judge path; no model endpoint.

namespace AgentMemory.Tests.Integration.Services;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class StoreSearchCaptureIntegrationTests(Neo4jIntegrationFixture fixture)
{
    [Fact]
    public async Task Capture_searches_the_real_store_keeps_owner_scope_and_does_not_persist_the_question()
    {
        await fixture.CleanDatabaseAsync();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(now);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        var options = new MemoryOptions();
        services.AddNeo4jAgentMemory(options, configureNeo4j: o =>
        {
            o.Uri = fixture.ConnectionString; o.Username = fixture.User; o.Password = fixture.Password;
            o.Database = "neo4j"; o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        });
        using var embeddings = new StubEmbeddingGenerator(NullLogger<StubEmbeddingGenerator>.Instance,
            Neo4jIntegrationFixture.TestEmbeddingDimensions);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddings);
        StoreSearchCapture.Register(services, options);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
        const string question = "Task status planned";
        var vector = (await embeddings.GenerateAsync([question]))[0].Vector.ToArray();
        foreach (var owner in new[] { "capture-owner", "other-owner" })
            await facts.UpsertAsync(new Fact
            {
                FactId = owner + "-fact", Subject = "Task", Predicate = "status", Object = "planned",
                Confidence = 1, CreatedAtUtc = now.AddDays(-1), OwnerId = owner, Embedding = vector,
            });
        var original = await facts.GetByIdAsync("capture-owner-fact");
        var capture = scope.ServiceProvider.GetRequiredService<StoreSearchCapture>();
        var row = await capture.CaptureAsync(new SearchCaptureQuery { Id = "case", Session = 1, Question = question },
            "capture-session", "capture-owner", 50);

        row.Error.Should().BeNull();
        row.Entries.Select(e => e.ItemId).Should().Contain("capture-owner-fact").And.NotContain("other-owner-fact");
        row.Context!.RelevantFacts.Diagnostics!.Searched.Should().BeTrue();
        row.Context.RelevantFacts.Diagnostics.Returned.Should().Be(1);
        row.Context.RelevantFacts.RankedItems.Single().ItemId.Should().Be("capture-owner-fact");
        var serialized = JsonSerializer.Serialize(row, SearchCapturePlan.Json);
        serialized.Should().Contain("capture-owner-fact").And.NotContain("other-owner-fact");
        (await facts.GetByIdAsync("capture-owner-fact")).Should().BeEquivalentTo(original);
        (await scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>().GetAllSessionMessagesAsync("capture-session"))
            .Should().BeEmpty("querying must not write the question into the evidence");

        var historical = await capture.CaptureAsync(new SearchCaptureQuery { Id = "history", Session = 1,
            Question = question, Historical = true }, "capture-session", "capture-owner", 50);
        historical.Error.Should().BeNull();
        historical.Request.Should().BeNull("historical retrieval is not gated");
        historical.AsOfUtc.Should().Be(now);
        historical.Entries.Select(e => e.ItemId).Should().Contain("capture-owner-fact").And.NotContain("other-owner-fact");
    }
}
