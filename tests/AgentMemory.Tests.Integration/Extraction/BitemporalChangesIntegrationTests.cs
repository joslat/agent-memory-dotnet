using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// 40.65 (the bitemporal recheck, 2026-10-03), on a real store: a change of value is recorded on the valid-time clock and
/// a correction on the transaction clock, so a question about the past still finds the value that was true then. Without
/// <see cref="ExtractionOptions.BitemporalChanges"/> every closing is a retraction, and "where did I live in 2020?", asked
/// after a move, loses the old city: that behaviour is pinned here too, so the option's difference is visible.
/// Scripted extraction, supersession on, no model.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class BitemporalChangesIntegrationTests : IAsyncLifetime
{
    private const string Owner = "bitemporal-probe";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset BilbaoSince = Now.AddYears(-16);
    private static readonly DateTimeOffset MadridSince = Now.AddMonths(-7);
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider? _provider;

    public BitemporalChangesIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_change_keeps_the_old_value_for_questions_about_the_past()
    {
        Build(bitemporal: true);
        await SayAsync("I live in Bilbao.");
        await SayAsync("I moved to Madrid in March.");

        (await CitiesAsOfAsync(Now.AddYears(-6), DateTimeOffset.UtcNow)).Should().Contain("Bilbao").And.NotContain("Madrid",
            "six years ago the person lived in Bilbao, and that is still believed");
        (await CitiesAsOfAsync(Now.AddMonths(-1), DateTimeOffset.UtcNow)).Should().Contain("Madrid").And.NotContain("Bilbao",
            "after the move the person lives in Madrid");
        (await LiveCitiesAsync()).Should().Equal(["Madrid"], "live recall is unchanged: only the current value");
    }

    [Fact]
    public async Task Without_the_option_a_change_loses_the_past()
    {
        Build(bitemporal: false);
        await SayAsync("I live in Bilbao.");
        await SayAsync("I moved to Madrid in March.");

        (await CitiesAsOfAsync(Now.AddYears(-6), DateTimeOffset.UtcNow)).Should().NotContain("Bilbao",
            "today's behaviour, pinned: the change was stored as a retraction, so a past question read with today's belief drops it");
    }

    [Fact]
    public async Task An_undated_change_starts_when_it_was_said()
    {
        Build(bitemporal: true);
        await SayAsync("Bilbao is home.");
        await Task.Delay(50);
        var between = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await SayAsync("I moved to Madrid.");

        (await CitiesAsOfAsync(between, DateTimeOffset.UtcNow)).Should().Contain("Bilbao").And.NotContain("Madrid",
            "before the move was said, the person lived in Bilbao; an undated new value starts when it was said");
    }

    [Fact]
    public async Task Without_the_option_an_undated_change_is_true_at_every_past_moment()
    {
        Build(bitemporal: false);
        await SayAsync("Bilbao is home.");
        await Task.Delay(50);
        var between = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await SayAsync("I moved to Madrid.");

        (await CitiesAsOfAsync(between, DateTimeOffset.UtcNow)).Should().Contain("Madrid").And.NotContain("Bilbao",
            "today's behaviour, pinned: an undated value has no start, so it answers for a moment before it was said");
    }

    [Fact]
    public async Task Belief_before_the_change_was_learned_still_sees_the_old_value_open()
    {
        Build(bitemporal: true);
        await SayAsync("I live in Bilbao.");
        await Task.Delay(50);
        var beforeTold = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await SayAsync("I moved to Madrid in March.");

        (await CitiesAsOfAsync(Now.AddMonths(-1), beforeTold)).Should().Contain("Bilbao").And.NotContain("Madrid",
            "believed before the move was told, the person still lived in Bilbao last month: the end was not known yet");
    }

    [Fact]
    public async Task A_correction_withdraws_belief_and_leaves_valid_time_alone()
    {
        Build(bitemporal: true);
        await SayAsync("Bilbao is home.");
        await Task.Delay(50);
        var beforeCorrection = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await SayAsync("Sorry, I meant Madrid, not Bilbao.");

        (await CitiesAsOfAsync(Now.AddYears(-1), DateTimeOffset.UtcNow)).Should().Contain("Madrid").And.NotContain("Bilbao",
            "Bilbao was never right, so it is not believed at any time");
        (await CitiesAsOfAsync(Now.AddYears(-1), beforeCorrection)).Should().Contain("Bilbao",
            "before the correction, Bilbao was what the system believed");

        var bilbao = await ReadAsync(
            "MATCH (f:Fact {owner_id: $owner, object: 'Bilbao'}) RETURN f.invalidated_reason AS reason, f.valid_until AS until",
            new { owner = Owner });
        bilbao.Should().ContainSingle();
        ValueExtensions.As<string>(bilbao[0]["reason"]).Should().Be("correction");
        bilbao[0]["until"].Should().BeNull("a correction does not say when Bilbao stopped being true: it never was");
    }

    [Fact]
    public async Task A_change_records_why_and_when_the_old_value_ended()
    {
        Build(bitemporal: true);
        await SayAsync("I live in Bilbao.");
        await SayAsync("I moved to Madrid in March.");

        var bilbao = await ReadAsync(
            "MATCH (f:Fact {owner_id: $owner, object: 'Bilbao'}) " +
            "RETURN f.invalidated_reason AS reason, f.valid_until AS until, f.valid_until_recorded_at AS recorded",
            new { owner = Owner });
        bilbao.Should().ContainSingle();
        ValueExtensions.As<string>(bilbao[0]["reason"]).Should().Be("change");
        ValueExtensions.As<ZonedDateTime>(bilbao[0]["until"]).ToDateTimeOffset().Should().BeCloseTo(MadridSince, TimeSpan.FromSeconds(1),
            "the old value ends when the new one began");
        bilbao[0]["recorded"].Should().NotBeNull("the end is itself something learned, at a known time");

        using var scope = _provider!.CreateScope();
        var history = await scope.ServiceProvider.GetRequiredService<IMemoryHistoryService>().GetHistoryAsync(
            new MemoryHistoryQuery { Kind = MemoryHistoryKind.Fact, OwnerId = Owner });
        history.Single(record => record.Summary.EndsWith("Bilbao", StringComparison.Ordinal)).ClosedAs.Should().Be("change",
            "fact history (and the MCP memory_lineage tool built on it) says why the old value was closed");
    }

    private void Build(bool bitemporal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddNeo4jAgentMemory(
            new MemoryOptions { Extraction = { SupersedeReplacedFacts = true, BitemporalChanges = bitemporal } },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    private static RecallRequest Question() => new()
    {
        SessionId = "s-bitemporal", UserId = Owner, Query = "Where does the user live?",
        Options = new RecallOptions
        {
            MaxRecentMessages = 0, MaxRelevantMessages = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
            MaxFacts = 10, MinSimilarityScore = 0,
        },
    };

    private async Task<IReadOnlyList<string>> CitiesAsOfAsync(DateTimeOffset validAsOf, DateTimeOffset systemAsOf)
    {
        using var scope = _provider!.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var context = (await memory.RecallAsOfAsync(Question(), validAsOf, systemAsOf, CancellationToken.None)).Context;
        return [.. context.RelevantFacts.Items.Where(f => f.Predicate == "lives in").Select(f => f.Object)];
    }

    private async Task<IReadOnlyList<string>> LiveCitiesAsync()
    {
        using var scope = _provider!.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var context = (await memory.RecallAsync(Question(), CancellationToken.None)).Context;
        return [.. context.RelevantFacts.Items.Where(f => f.Predicate == "lives in").Select(f => f.Object)];
    }

    private async Task<List<IRecord>> ReadAsync(string cypher, object parameters)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, parameters);
        return await cursor.ToListAsync();
    }

    private async Task SayAsync(string text)
    {
        using var scope = _provider!.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        await shortTerm.AddConversationAsync("conv-bitemporal", "s-bitemporal", userId: Owner);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}", ConversationId = "conv-bitemporal", SessionId = "s-bitemporal",
            Role = "user", Content = text, TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest { SessionId = "s-bitemporal", UserId = Owner, Messages = [message] });
    }

    /// <summary>What each line says, as a model would extract it.</summary>
    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.Select(m => m.Content switch
            {
                "I live in Bilbao." => Lives("Bilbao") with { ValidFrom = BilbaoSince, ValidFromPrecision = DatePrecision.Year },
                "I moved to Madrid in March." => Lives("Madrid") with { ValidFrom = MadridSince, ValidFromPrecision = DatePrecision.Month },
                "Bilbao is home." => Lives("Bilbao"),
                "I moved to Madrid." => Lives("Madrid"),
                "Sorry, I meant Madrid, not Bilbao." => Lives("Madrid") with { Replaces = "Bilbao" },
                _ => throw new InvalidOperationException($"No script for '{m.Content}'."),
            })]);

        private static ExtractedFact Lives(string city) =>
            new() { Subject = "user", Predicate = "lives in", Object = city, Confidence = 0.95 };
    }
}
