using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;
using MemoryFact = AgentMemory.Abstractions.Domain.Fact;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G5-BUG1. Predicate expansion returns a relation across the whole owner, so an expanded fact may
/// carry provenance outside the current question's message window. That is expected; a fact whose
/// source genuinely cannot be resolved is corruption. Conflating them made every question fail with
/// retrieval-diagnostics-error before a single answer call.
/// </summary>
public sealed class LongMemEvalExpandedFactEvidenceTests
{
    [Fact]
    public void AnExpandedFactWithOutOfWindowProvenanceIsAccepted()
    {
        var context = ContextWith(Fact("expanded", "outside-window", expanded: true));

        var act = () => LongMemEvalAgentEvalEvidence.Build(
            context, Origins(), LongMemEvalEvidenceDetail.Identifiers);

        act.Should().NotThrow();
    }

    [Fact]
    public void AnUnmarkedFactWithUnresolvableProvenanceStillThrows()
    {
        // The guard exists to catch provenance corruption and is deliberately untouched: only the
        // new, legitimate category is exempted.
        var context = ContextWith(Fact("ordinary", "outside-window", expanded: false));

        var act = () => LongMemEvalAgentEvalEvidence.Build(
            context, Origins(), LongMemEvalEvidenceDetail.Identifiers);

        act.Should().Throw<InvalidOperationException>().WithMessage("*could not map source message*");
    }

    [Fact]
    public void AnExpandedFactWhoseProvenanceIsInWindowIsStillAccepted()
    {
        var context = ContextWith(Fact("expanded-inside", "known-message", expanded: true));

        var act = () => LongMemEvalAgentEvalEvidence.Build(
            context, Origins(), LongMemEvalEvidenceDetail.Identifiers);

        act.Should().NotThrow();
    }

    private static MemoryFact Fact(string id, string sourceMessageId, bool expanded) => new()
    {
        FactId = id,
        Subject = "s",
        Predicate = "was_born",
        Object = "o",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        SourceMessageIds = [sourceMessageId],
        Metadata = expanded
            ? new Dictionary<string, object>
            {
                [MemoryFact.RetrievalSourceMetadataKey] = MemoryFact.RetrievalSourcePredicateExpansion
            }
            : new Dictionary<string, object>()
    };

    private static MemoryContext ContextWith(params MemoryFact[] facts) => new()
    {
        SessionId = "s",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantFacts = new MemoryContextSection<MemoryFact> { Items = facts }
    };

    private static IReadOnlyDictionary<string, LongMemEvalMessageOrigin> Origins() =>
        new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal)
        {
            ["known-message"] = new(0, "session-1", 0, 0, "2023/05/20 (Sat) 10:19", "user",
                "known-message", false, false, false)
        };
}
