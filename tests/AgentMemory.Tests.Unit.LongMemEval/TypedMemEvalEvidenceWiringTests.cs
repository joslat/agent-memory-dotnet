using System.Reflection;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.9c prereq B: the evidence envelope works for TypedMemEval's <b>embedded</b> corpora.
/// </summary>
/// <remarks>
/// <para>
/// The envelope path previously required an evidence index built from a dataset <b>file</b>
/// (<c>LongMemEvalEvidenceIndex.Load(datasetPath, …)</c>). TypedMemEval corpora are embedded
/// resources, so every typed run necessarily ran without an index, attached no envelope, and
/// reported attribution <c>Unobserved</c> — the free-checks pass named this exact gap. These tests
/// pin the new construction path end to end: corpus-sourced entries, the replicated option
/// mapping, the QueryTime-salted fingerprint the Prospective pairs require, and a full offline
/// stub-agent + stub-judge run whose attribution is observed.
/// </para>
/// <para>
/// Version-agnostic by design: no corpus SHA-256 or question-count literal appears here — counts,
/// ids and hashes are read from <see cref="TypedMemEvalCorpus"/> at run time, so the 0.23 corpus
/// revision (new ids, new hashes) must not touch this file.
/// </para>
/// </remarks>
public sealed class TypedMemEvalEvidenceWiringTests
{
    [Fact]
    public void OptionMappingMatchesAgentEvalsInternalMapper_ForEveryVerticalAndArm()
    {
        // The runner maps its facade through an INTERNAL method this harness cannot call, so the
        // harness replicates the mapping — and a replica without a drift guard is a lie waiting for
        // the next AgentEval release. This invokes the real internal mapper by reflection and holds
        // the replica to property-for-property equality across every vertical and both arms.
        var toExternal = typeof(TypedMemEvalOptions).GetMethod(
            "ToExternalOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        toExternal.Should().NotBeNull(
            "the pinned AgentEval package is expected to map its facade through " +
            "TypedMemEvalOptions.ToExternalOptions; if this fails after a version bump, re-verify " +
            "the mapping and update TypedMemEvalOptionMapping to match");

        TypedMemEvalOptions[] facades =
        [
            new(),
            new()
            {
                MaxQuestions = 7,
                RandomSeed = 3,
                AnswerSeed = -2,
                AnswerTemperature = 0.5,
                JudgeTemperature = 0.1,
                RetainRawJudgeResponse = true,
                EvidenceTopK = 25
            },
            new() { TemporalGrounding = TemporalGroundingMode.TimestampsOnly },
            new() { TemporalGrounding = TemporalGroundingMode.TimestampsAndText, ControlArm = true },
            new() { IncludeTimestamps = false },
        ];

        foreach (var descriptor in TypedMemEvalVerticals.All)
        foreach (var facade in facades)
        {
            var expected = (ExternalBenchmarkOptions)toExternal!.Invoke(facade, [descriptor])!;
            var actual = TypedMemEvalOptionMapping.ToExternalOptions(facade, descriptor);
            actual.Should().BeEquivalentTo(
                expected,
                because: $"the replica must match AgentEval's own mapping for {descriptor.Slug}");
        }
    }

    [Fact]
    public async Task OfflineTypedRunReportsObservedAttribution_OnceTheEmbeddedIndexIsWired()
    {
        // The free-checks probe proved this exact offline path runs (Forgetting, MaxQuestions=4,
        // stub judge answering {"outcome":"abstained"}) — and that without an index its attribution
        // is Unobserved on every question. This is the same run with the index wired.
        var facade = new TypedMemEvalOptions { MaxQuestions = 4, RandomSeed = 20260815 };
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message> lastStored = [];
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lastStored = call.Arg<IEnumerable<Message>>().ToArray();
                return lastStored;
            });
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                var items = lastStored.Take(5).ToArray();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = items,
                            RankedItems = items
                                .Select((message, index) => new MemoryContextRankedItem(
                                    message.MessageId, 0.9 - index * 0.01, index + 1, index + 1))
                                .ToArray()
                        }
                    },
                    TotalItemsRetrieved = items.Length
                };
            });
        var answerChat = Substitute.For<IChatClient>();
        answerChat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "I don't have that information.")));
        var judgeChat = Substitute.For<IChatClient>();
        judgeChat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, """{"outcome":"abstained"}""")));

        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory,
            answerChat,
            "typed-offline",
            new LongMemEvalAdapterOptions
            {
                // THE wiring under test: an index built from the embedded corpus, not from a file.
                EvidenceIndex = LongMemEvalEvidenceIndex.CreateTypedMemEval(
                    TypedMemEvalVertical.Forgetting, facade)
            });
        var result = await new TypedMemEvalRunner(judgeChat)
            .RunAsync(adapter, TypedMemEvalVertical.Forgetting, facade);

        result.QuestionResults.Should().NotBeEmpty();
        result.QuestionResults.Should().OnlyContain(question =>
            question.ExecutionStatus == QuestionExecutionStatus.Completed);
        // The point of the prerequisite: with the embedded-corpus index wired, the adapter attaches
        // the envelope and NOT ONE question reports the attribution channel as unobserved.
        result.QuestionResults.Should().OnlyContain(question =>
            question.TypedOutcome != null &&
            question.TypedOutcome!.Attribution != TypedMemEvalEvidenceAttribution.Unobserved);
        result.TypedOutcomes.Should().NotBeNull();
        result.TypedOutcomes!.Attribution.Unobserved.Should().Be(0);
        result.TypedOutcomes.Attribution.ObservedShare.Should().Be(1.0);
    }

    [Fact]
    public void ProspectivePairArms_ResolveDistinctly_BecauseTheFingerprintCarriesQueryTime()
    {
        // Two Prospective pair arms are one haystack and one question text asked at two instants.
        // A fingerprint over the turns alone makes them a single entry with two candidate
        // questions and an identical prompt — unresolvable. The query instant is identity.
        var options = TypedMemEvalOptionMapping.ToExternalOptions(
            new TypedMemEvalOptions { TemporalGrounding = TemporalGroundingMode.TimestampsOnly },
            TypedMemEvalVerticals.For(TypedMemEvalVertical.Prospective));
        var arms = new[]
        {
            PairArm("tme-test-a", "2026/06/04 (Thu) 21:30"),
            PairArm("tme-test-b", "2026/06/16 (Tue) 21:30"),
        };
        var index = LongMemEvalEvidenceIndex.CreateTimestamped(arms, options);

        foreach (var arm in arms)
        {
            var history = LongMemEvalHistoryFormatter.FormatTimestamped(arm, options);
            var pairs = history.Turns
                .Select(turn => (turn.UserMessage, turn.AssistantResponse))
                .ToArray();

            var resolved = index.Resolve(pairs, history.QueryTime, arm.Question);

            resolved.QuestionId.Should().Be(arm.QuestionId);
            resolved.QuestionDate.Should().Be(arm.QuestionDate);
        }
    }

    [Fact]
    public void CreateTypedMemEval_AlignsWithTheRunnersTimestampedInjection_ForProspective()
    {
        // Selection, formatting, and prompt construction must all reproduce what the runner will
        // inject, or Resolve throws mid-run. This drives the real embedded Prospective corpus
        // through the same seeded selection and resolves every drawn question.
        var facade = new TypedMemEvalOptions { MaxQuestions = 3, RandomSeed = 7 };
        var descriptor = TypedMemEvalVerticals.For(TypedMemEvalVertical.Prospective);
        var options = TypedMemEvalOptionMapping.ToExternalOptions(facade, descriptor);
        var entries = TypedMemEvalCorpus.Load(TypedMemEvalVertical.Prospective, options);
        var index = LongMemEvalEvidenceIndex.CreateTypedMemEval(
            TypedMemEvalVertical.Prospective, facade);

        entries.Should().NotBeEmpty();
        foreach (var entry in entries)
        {
            var history = LongMemEvalHistoryFormatter.FormatTimestamped(entry, options);
            var pairs = history.Turns
                .Select(turn => (turn.UserMessage, turn.AssistantResponse))
                .ToArray();

            // Under TimestampsOnly the runner sends the bare question — no "Current Date:" prefix.
            var resolved = index.Resolve(pairs, history.QueryTime, entry.Question);

            resolved.QuestionId.Should().Be(entry.QuestionId);
        }
    }

    private static LongMemEvalEntry PairArm(string id, string questionDate) => new()
    {
        QuestionId = id,
        QuestionType = "prospective",
        Question = "Has the reminder about the lease renewal come due yet?",
        AnswerRaw = JsonSerializer.SerializeToElement("yes"),
        QuestionDate = questionDate,
        HaystackDates = ["2026/05/01 (Fri) 09:00"],
        HaystackSessionIds = ["pair-session-1"],
        HaystackSessions =
        [
            [
                new LongMemEvalTurn
                {
                    Role = "user",
                    Content = "Remind me to renew the lease on June 10.",
                    HasAnswer = true
                },
                new LongMemEvalTurn { Role = "assistant", Content = "I will remind you." }
            ]
        ],
        AnswerSessionIds = ["pair-session-1"]
    };
}
