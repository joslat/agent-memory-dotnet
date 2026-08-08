using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G5-BUG1, real cause. AgentEval rejects an evidence envelope above 100 references, and that cap
/// counts entities, facts <b>and</b> preferences — not the fact budget alone. A structured arm
/// already spends ~30 before expansion, so the old 100-fact default guaranteed overflow: nine of ten
/// questions failed mid-run with an opaque diagnostics error, and the one that survived was simply
/// the one whose expansion returned few enough facts.
/// </summary>
public sealed class LongMemEvalExpansionBudgetTests
{
    [Fact]
    public void AnExpansionBudgetThatWouldOverflowTheEvidenceEnvelopeIsRejectedAtConstruction()
    {
        // The point of failing here: the overflow previously surfaced only after a 121-call,
        // 22-minute rebuild had already been paid for.
        var act = () => Create(expandedFacts: 100);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*evidence references*maximum*");
    }

    [Fact]
    public void TheDefaultExpansionBudgetFitsTheEnvelope()
    {
        var act = () => Create(expandedFacts: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ABudgetThatExactlyFillsTheEnvelopeIsAccepted()
    {
        // Structured at 30 spends 10 + 10 + 10; 70 more reaches exactly the 100-reference cap.
        var act = () => Create(expandedFacts: 70);

        act.Should().NotThrow();
    }

    [Fact]
    public void OneOverTheEnvelopeIsRejected()
    {
        var act = () => Create(expandedFacts: 71);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NoLimitIsImposedWhenExpansionIsOff()
    {
        // Without expansion the envelope cannot overflow, so the budget is irrelevant and must not
        // block an otherwise valid configuration.
        var act = () => Create(expandedFacts: 1_000, expand: false);

        act.Should().NotThrow();
    }

    private static AgentMemoryLongMemEvalAdapter Create(int? expandedFacts, bool expand = true)
    {
        var options = new LongMemEvalAdapterOptions
        {
            MemoryMode = LongMemEvalMemoryMode.Structured,
            MaxRelevantMessages = 30,
            ExpandFactsByPredicate = expand
        };
        if (expandedFacts is { } value)
            options = options with { MaxExpandedFacts = value };

        return new AgentMemoryLongMemEvalAdapter(
            Substitute.For<IMemoryService>(),
            Substitute.For<IChatClient>(),
            "budget-run",
            options);
    }
}
