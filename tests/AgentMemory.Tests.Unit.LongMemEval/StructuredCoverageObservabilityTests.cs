using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Gold-session coverage must be observable on the structured arm (22.3).
/// </summary>
/// <remarks>
/// <para>
/// Gold attribution used to ride entirely on recalled raw messages, so a structured run — which has
/// no message budget — reported every gold metric as unobservable. That was honest and it was
/// blinding: <c>GoldSessionRecallAtK</c> was null on <b>1,476 of 1,476</b> structured
/// question-records, and gold-session recall is exactly the coverage the completeness sweep measured
/// to be worth eighty accuracy points.
/// </para>
/// <para>
/// Structured items carry their own <c>SourceMessageIds</c>, so the attribution is resolvable without
/// raw messages. These tests pin that it happens, that it does not double-count, and that a genuinely
/// unattributable run still reports null rather than a flattering zero.
/// </para>
/// </remarks>
public sealed class StructuredCoverageObservabilityTests
{
    private const string Gold = "gold-session";
    private const string Other = "other-session";

    private static LongMemEvalEvidenceQuestion Question() =>
        new("q1", "multi-session", "How many?", "How many?", "2", "2023/05/30 (Tue) 12:00",
            IsAbstention: false,
            new HashSet<string>(StringComparer.Ordinal) { Gold },
            AnnotatedGoldTurnCount: 2,
            [Origin(0, Gold), Origin(1, Other)]);

    private static LongMemEvalMessageOrigin Origin(int ordinal, string sessionId) =>
        new(ordinal, sessionId, 0, ordinal, "2023/05/30 (Tue) 12:00", "user", $"m{ordinal}",
            false, false, false);

    private static Dictionary<string, LongMemEvalMessageOrigin> Origins() => new(StringComparer.Ordinal)
    {
        ["m0"] = Origin(0, Gold),
        ["m1"] = Origin(1, Other),
    };

    private static LongMemEvalRetrievalEvidence Build(
        IReadOnlyCollection<string>? structuredIds, int messageBudget = 0) =>
        LongMemEvalRetrievalEvidence.Build(
            Question(),
            recalled: [],
            rankedItems: [],
            Origins(),
            LongMemEvalEvidenceDetail.Identifiers,
            answerPromptCharacters: 100,
            configuredMessageBudget: messageBudget,
            structuredSourceMessageIds: structuredIds);

    [Fact]
    public void AStructuredRunReportsCoverageInsteadOfNull()
    {
        // THE fix. Zero message budget, zero recalled messages -- and a retrieved fact whose
        // provenance points at the gold session. Before 22.3 this returned null and the arm the
        // project actually ships was invisible to its own coverage metric.
        var evidence = Build(["m0"]);

        evidence.GoldSessionRecallAtK.Should().Be(1.0);
        evidence.GoldSessionsHit.Should().Be(1);
    }

    [Fact]
    public void ProvenanceIntoANonGoldSessionIsObservableButNotCoverage()
    {
        // Retrieval happened and missed. That must read as coverage 0.0, NOT as unobservable --
        // conflating "retrieved the wrong thing" with "could not tell" is what made every structured
        // failure unattributable.
        var evidence = Build(["m1"]);

        evidence.GoldSessionRecallAtK.Should().Be(0.0);
        evidence.GoldSessionsHit.Should().Be(0);
    }

    [Fact]
    public void ARunWithNoResolvableProvenanceIsStillUnobservable()
    {
        // The distinction the original guard existed to protect, and it must survive the fix: with
        // nothing to attribute through, the honest answer is null. A zero here would manufacture a
        // retrieval miss out of a harness limitation -- the exact defect the doc comment records.
        Build(structuredIds: []).GoldSessionRecallAtK.Should().BeNull();
        Build(structuredIds: null).GoldSessionRecallAtK.Should().BeNull();
    }

    [Fact]
    public void AnUnknownMessageIdDoesNotCountAsCoverage()
    {
        // Provenance pointing at something outside this question's origins cannot be resolved, so it
        // must neither count nor make the run look observable.
        Build(["not-a-real-id"]).GoldSessionRecallAtK.Should().BeNull();
    }

    [Fact]
    public void TheSameSessionReachedTwiceIsCountedOnce()
    {
        // Union, not sum. A gold session reached through several retrieved facts is one session
        // covered; summing would report recall above 1.0 and make the metric meaningless exactly
        // where it matters most -- the hybrid arm, which has both channels.
        var evidence = Build(["m0", "m0"]);

        evidence.GoldSessionsHit.Should().Be(1);
        evidence.GoldSessionRecallAtK.Should().Be(1.0);
    }

    [Fact]
    public void TheStructuredExtractorPullsProvenanceFromAllThreeCategories()
    {
        // A category left out of the collector is a silent under-count of coverage, which would read
        // as a retrieval defect rather than a harness one.
        var now = DateTimeOffset.UtcNow;
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = now,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = [new Entity
                {
                    EntityId = "e1", Name = "n", Type = "t", Confidence = 1, CreatedAtUtc = now,
                    SourceMessageIds = ["m-entity"],
                }],
            },
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = [new Fact
                {
                    FactId = "f1", Subject = "s", Predicate = "p", Object = "o", Confidence = 1,
                    CreatedAtUtc = now, SourceMessageIds = ["m-fact"],
                }],
            },
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = [new Preference
                {
                    PreferenceId = "p1", Category = "c", PreferenceText = "t", Confidence = 1,
                    CreatedAtUtc = now, SourceMessageIds = ["m-pref"],
                }],
            },
        };

        AgentMemoryLongMemEvalAdapter.StructuredSourceMessageIds(context)
            .Should().BeEquivalentTo(["m-entity", "m-fact", "m-pref"]);
    }
}
