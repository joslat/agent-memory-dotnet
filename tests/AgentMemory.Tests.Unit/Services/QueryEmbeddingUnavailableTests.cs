using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// A query whose embedding could not be made is not searched; the recall must say so, or an embedding outage reads
/// exactly like an owner with no memories. The orchestrator hands back an empty vector both for "nothing to embed" and
/// for a failed generation, so the assembler is where the two are told apart.
/// </summary>
public sealed class QueryEmbeddingUnavailableTests
{
    private static readonly DateTimeOffset Instant = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_failed_query_embedding_is_reported_on_the_live_path()
    {
        var assembler = Create(Embeds(Array.Empty<float>()));

        var context = await assembler.AssembleContextAsync(Request("where do I work?"), CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeTrue("the query needed a vector, was not blank, and none came back");
    }

    [Fact]
    public async Task A_failed_query_embedding_is_reported_on_the_as_of_path()
    {
        var assembler = Create(Embeds(Array.Empty<float>()));

        var context = await assembler.AssembleContextAsOfAsync(Request("where did I work?"), Instant, Instant, CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_query_that_embedded_is_not_flagged()
    {
        var assembler = Create(Embeds([0.1f, 0.2f, 0.3f]));

        var context = await assembler.AssembleContextAsync(Request("where do I work?"), CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_blank_query_had_nothing_to_embed_and_is_not_flagged()
    {
        var assembler = Create(Embeds(Array.Empty<float>()));

        var context = await assembler.AssembleContextAsync(Request("   "), CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeFalse();
    }

    [Fact]
    public async Task A_caller_supplied_vector_is_not_flagged_and_nothing_is_embedded()
    {
        var orchestrator = Embeds(Array.Empty<float>());
        var assembler = Create(orchestrator);

        var context = await assembler.AssembleContextAsync(
            Request("where do I work?") with { QueryEmbedding = [0.5f, 0.5f, 0f] }, CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeFalse();
        await orchestrator.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default);
    }

    [Fact]
    public async Task A_recall_that_needs_no_vector_is_not_flagged()
    {
        var assembler = Create(Embeds(Array.Empty<float>()));
        var request = Request("where do I work?");

        var context = await assembler.AssembleContextAsync(
            request with { Options = request.Options with { MaxRelevantMessages = 0 } }, CancellationToken.None);

        context.QueryEmbeddingUnavailable.Should().BeFalse();
    }

    private static IEmbeddingOrchestrator Embeds(float[] vector)
    {
        var orchestrator = Substitute.For<IEmbeddingOrchestrator>();
        orchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(vector);
        return orchestrator;
    }

    private static RecallRequest Request(string query) => new()
    {
        SessionId = "s1",
        UserId = "u1",
        Query = query,
        Options = new RecallOptions
        {
            MaxRelevantMessages = 5, MaxRecentMessages = 0, MaxFacts = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
        },
    };

    private static MemoryContextAssembler Create(IEmbeddingOrchestrator orchestrator)
    {
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        shortTerm.GetRecentMessagesAsOfAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Message>());
        var options = new MemoryOptions();
        return new MemoryContextAssembler(
            shortTerm,
            Substitute.For<ILongTermMemoryService>(),
            Substitute.For<IReasoningMemoryService>(),
            graphRag: null,
            orchestrator,
            Substitute.For<TimeProvider>(),
            Options.Create(options),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(Options.Create(options.Isolation), NullLogger<DefaultMemoryIsolationPolicy>.Instance));
    }
}
