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
/// Degrading under load without pretending otherwise (rank 13).
/// </summary>
/// <remarks>
/// <para>
/// A recall latency budget is easy to add and easy to get dangerously wrong. The dangerous version
/// returns whatever arrived in time and says nothing: a section cut short reports <c>Searched</c> and
/// <c>Returned = 0</c>, which is byte-identical to a section that ran to completion and genuinely
/// found nothing.
/// </para>
/// <para>
/// <b>A partial recall that looks complete is worse than a slow one.</b> The caller answers
/// confidently from memory that was never consulted, and nothing anywhere records that it happened.
/// So most of what follows is about the marking, not the timing.
/// </para>
/// </remarks>
public sealed class RecallLatencyBudgetTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();

    private static readonly Message AMessage = new()
    {
        MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1",
        Role = "user", Content = "hello", TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    public RecallLatencyBudgetTests()
    {
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<float[]>([0.1f, 0.2f]));

        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([AMessage]));
        _shortTerm.SearchMessagesAsync(
                Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>([]));

        _longTerm.SearchPreferencesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>([]));
        _reasoning.SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));
    }

    /// <summary>Entities return promptly; facts hang past any sane budget.</summary>
    private void FactsAreSlow(TimeSpan delay)
    {
        _longTerm.SearchEntitiesAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(
            [
                new Entity
                {
                    EntityId = "e-1", Name = "Alice", Type = "Person",
                    Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UnixEpoch,
                },
            ]));

        _longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                return (IReadOnlyList<Fact>)[];
            });
    }

    private MemoryContextAssembler CreateSut() =>
        new(_shortTerm, _longTerm, _reasoning,
            graphRag: null,
            _embeddings,
            Substitute.For<IClock>(),
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance,
            Substitute.For<IMemoryIsolationPolicy>());

    private Task<MemoryContext> AssembleAsync(TimeSpan? budget) =>
        CreateSut().AssembleContextAsync(new RecallRequest
        {
            SessionId = "s-1",
            Query = "what do I know",
            Options = new RecallOptions
            {
                IncludeDiagnostics = true,
                LatencyBudget = budget,
            },
        });

    [Fact]
    public async Task ASlowSectionIsDroppedRatherThanWaitedFor()
    {
        FactsAreSlow(TimeSpan.FromSeconds(30));

        var context = await AssembleAsync(TimeSpan.FromMilliseconds(150));

        context.LatencyBudgetExceeded.Should().BeTrue();
    }

    [Fact]
    public async Task TheDroppedSectionSaysSoAndTheOthersDoNot()
    {
        // THE test. Without this, "cut short" and "searched and found nothing" are the same section,
        // and degradation under load is invisible in every artifact and every log.
        FactsAreSlow(TimeSpan.FromSeconds(30));

        var context = await AssembleAsync(TimeSpan.FromMilliseconds(150));

        context.RelevantFacts.Diagnostics!.TimedOut.Should().BeTrue();
        context.RelevantFacts.Diagnostics.AbandonedToLatencyBudget.Should().BeTrue();
        context.RelevantEntities.Diagnostics!.TimedOut.Should().BeFalse(
            "a section that finished in time must not be tarred with the budget failure");
    }

    [Fact]
    public async Task WhatArrivedInTimeIsStillReturned()
    {
        // The point of degrading rather than failing: a partial answer beats no answer, provided the
        // caller can tell it is partial.
        FactsAreSlow(TimeSpan.FromSeconds(30));

        var context = await AssembleAsync(TimeSpan.FromMilliseconds(150));

        context.RelevantEntities.Items.Should().ContainSingle();
        context.RecentMessages.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task NoBudgetWaitsForEverything()
    {
        // The off state, and it is the default. Every measurement in this track was taken without a
        // budget, and a slow section must still be waited for rather than quietly dropped.
        FactsAreSlow(TimeSpan.FromMilliseconds(200));

        var context = await AssembleAsync(budget: null);

        context.LatencyBudgetExceeded.Should().BeFalse();
        context.RelevantFacts.Diagnostics!.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task AGenerousBudgetChangesNothing()
    {
        // A budget nobody exceeds must leave the context identical to having no budget at all.
        FactsAreSlow(TimeSpan.FromMilliseconds(10));

        var context = await AssembleAsync(TimeSpan.FromSeconds(30));

        context.LatencyBudgetExceeded.Should().BeFalse();
        context.RelevantEntities.Items.Should().ContainSingle();
    }

    [Fact]
    public void TheBudgetIsOffByDefault() =>
        new RecallOptions().LatencyBudget.Should().BeNull();

    [Fact]
    public void PartialIsNotTheSameFlagAsTruncated()
    {
        // Truncation is the context budget trimming memories that WERE retrieved. This is a retrieval
        // that never came back. A caller told only "truncated" would conclude the memories exist and
        // were dropped to fit, when they may never have been looked at.
        var context = new MemoryContext
        {
            SessionId = "s-1",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            LatencyBudgetExceeded = true,
        };

        context.LatencyBudgetExceeded.Should().BeTrue();
        context.Truncated.Should().BeFalse();
    }
}
