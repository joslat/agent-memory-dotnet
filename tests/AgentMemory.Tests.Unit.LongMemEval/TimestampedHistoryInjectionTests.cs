using AgentEval.Core;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.9c prereq A: the adapter accepts TypedMemEval's timestamped channel
/// (<see cref="ITimestampedHistoryInjectableAgent"/>) and honours its two clocks.
/// </summary>
/// <remarks>
/// <para>
/// The free-checks pass found this interface implemented <b>nowhere</b>: the tg work delivered
/// dates as text, and AgentEval's Prospective vertical refuses to run — before its first provider
/// call, deliberately — against an agent without the typed channel. These tests pin the semantics
/// the channel promises: a turn's instant becomes the stored message's own valid time, no date
/// text is ever added to message content (TimestampsOnly grounding exists precisely to remove the
/// in-text crutch), and the history's QueryTime anchors the recall and the answer call.
/// </para>
/// </remarks>
public sealed class TimestampedHistoryInjectionTests
{
    private static readonly DateTimeOffset FirstTurnTime =
        new(2023, 5, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SecondTurnTime =
        new(2023, 6, 11, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset QueryTime =
        new(2023, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimestampedConversationHistory TwoTurnHistory() => new()
    {
        Turns =
        [
            new TimestampedConversationTurn(
                "Alice moved to Zurich in March.", "Noted.", FirstTurnTime, 0),
            new TimestampedConversationTurn(
                "Remind me to renew the allotment lease.", "Will do.", SecondTurnTime, 1)
        ],
        QueryTime = QueryTime
    };

    private static IChatClient AnsweringChat(Action<IReadOnlyList<ChatMessage>>? capture = null)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capture?.Invoke(call.Arg<IEnumerable<ChatMessage>>().ToArray());
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer"));
            });
        return chat;
    }

    private static RecallResult RecallOf(string sessionId, params Message[] items) => new()
    {
        Context = new MemoryContext
        {
            SessionId = sessionId,
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantMessages = new MemoryContextSection<Message> { Items = items }
        },
        TotalItemsRetrieved = items.Length
    };

    private static Message Message(string sessionId, string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        SessionId = sessionId,
        ConversationId = sessionId,
        Role = role,
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public async Task StoresTurnInstantsAsMessageValidTime_AndAppendsNoDateTextToContent()
    {
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message>? stored = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<IEnumerable<Message>>().ToArray();
                return stored;
            });
        memory.RecallAsOfAsync(
                Arg.Any<RecallRequest>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => RecallOf(
                call.Arg<RecallRequest>().SessionId,
                Message(call.Arg<RecallRequest>().SessionId, "user", "Alice moved to Zurich in March.")));

        var adapter = new AgentMemoryLongMemEvalAdapter(memory, AnsweringChat(), "typed-run");
        await adapter.ResetSessionAsync();
        adapter.InjectTimestampedConversationHistory(TwoTurnHistory());

        await adapter.InvokeAsync("Where does Alice live?");

        stored.Should().NotBeNull().And.HaveCount(4);
        // (a) The turn's instant is the stored message's own clock — the product's valid time —
        // for BOTH halves of the pair, replacing the epoch + ordinal counter.
        stored!.Select(message => message.TimestampUtc).Should().Equal(
            FirstTurnTime, FirstTurnTime, SecondTurnTime, SecondTurnTime);
        // (b) The content is byte-identical to the injected turns: no session-date header, no
        // "Current Date:" line, no timestamp rendered into the text. Dates are structural here.
        stored.Select(message => message.Content).Should().Equal(
            "Alice moved to Zurich in March.",
            "Noted.",
            "Remind me to renew the allotment lease.",
            "Will do.");
    }

    [Fact]
    public async Task AnchorsRecallAndAnswerAtQueryTime()
    {
        var testStartUtc = DateTimeOffset.UtcNow;
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        DateTimeOffset? recordedAsOf = null;
        DateTimeOffset? recordedSystemAsOf = null;
        RecallRequest? recordedRequest = null;
        memory.RecallAsOfAsync(
                Arg.Any<RecallRequest>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordedRequest = call.Arg<RecallRequest>();
                recordedAsOf = call.ArgAt<DateTimeOffset>(1);
                recordedSystemAsOf = call.ArgAt<DateTimeOffset?>(2);
                return RecallOf(
                    recordedRequest.SessionId,
                    Message(recordedRequest.SessionId, "user", "Remind me to renew the allotment lease."));
            });
        IReadOnlyList<ChatMessage>? answerPrompt = null;
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, AnsweringChat(prompt => answerPrompt = prompt), "typed-run");
        await adapter.ResetSessionAsync();
        adapter.InjectTimestampedConversationHistory(TwoTurnHistory());

        await adapter.InvokeAsync("Has the lease renewal come due yet?");

        // (c) QueryTime reaches the recall path as the VALID-time clock: "what was true at the
        // question's now". The ordinary recall path must not run at all for a timestamped question.
        recordedAsOf.Should().Be(QueryTime);
        recordedRequest.Should().NotBeNull();
        await memory.DidNotReceive().RecallAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        // The TRANSACTION clock stays at the machine's now: the corpus was ingested moments ago, so
        // a transaction clock bound to the 2023 QueryTime would erase everything just stored.
        recordedSystemAsOf.Should().NotBeNull();
        recordedSystemAsOf!.Value.Should().BeOnOrAfter(testStartUtc);
        // The answer prompt's "now" is rendered from the typed channel's QueryTime — data the
        // system under test legitimately holds — not from evaluator-side knowledge.
        answerPrompt.Should().NotBeNull();
        answerPrompt!.Select(message => message.Text).Should().Contain(text =>
            text!.Contains(
                $"Current date: {AgentMemoryLongMemEvalAdapter.FormatQueryTime(QueryTime)}",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetClearsTheTimestampedState_SoThePlainChannelKeepsItsOwnClocks()
    {
        var memory = Substitute.For<IMemoryService>();
        IReadOnlyList<Message>? stored = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<IEnumerable<Message>>().ToArray();
                return stored;
            });
        memory.RecallAsOfAsync(
                Arg.Any<RecallRequest>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => RecallOf(
                call.Arg<RecallRequest>().SessionId,
                Message(call.Arg<RecallRequest>().SessionId, "user", "one")));
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => RecallOf(
                call.Arg<RecallRequest>().SessionId,
                Message(call.Arg<RecallRequest>().SessionId, "user", "two")));
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, AnsweringChat(), "typed-run");

        // Injected but never invoked — the runner's shape when a question dies between injection
        // and the agent call. Only ResetSessionAsync stands between this question's clocks and the
        // next one's: InvokeAsync consumes the pending state itself, so a sequence that invokes
        // before resetting cannot observe whether reset clears anything.
        await adapter.ResetSessionAsync();
        adapter.InjectTimestampedConversationHistory(TwoTurnHistory());
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("two", "second")]);
        await adapter.InvokeAsync("second question");

        // A leaked QueryTime would send the plain question down RecallAsOfAsync; a leaked turn
        // clock would stamp the plain question's messages with the previous corpus's dates.
        await memory.Received(1).RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        await memory.DidNotReceive().RecallAsOfAsync(
            Arg.Any<RecallRequest>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
        stored.Should().NotBeNull();
        stored!.Select(message => message.TimestampUtc).Should().Equal(
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1));
    }

    [Fact]
    public void DoubleInjectionIsRefused_AcrossChannels()
    {
        var adapter = new AgentMemoryLongMemEvalAdapter(
            Substitute.For<IMemoryService>(), Substitute.For<IChatClient>(), "typed-run");
        adapter.InjectConversationHistory([("one", "first")]);

        var act = () => adapter.InjectTimestampedConversationHistory(TwoTurnHistory());

        act.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 3)]
    public void RefusesOptionsThePointInTimeRecallPathWouldSilentlyIgnore(
        bool expandFactsByPredicate, bool resolveQueryRelations, int graphRagItems)
    {
        // The dead-option shape: RecallAsOfAsync implements neither predicate expansion nor
        // query-relation resolution nor GraphRAG, so a timestamped run configured with them would
        // report a measurement of options that never executed. ResolveQueryRelations rides with
        // expansion enabled so the combination is constructible at all.
        var adapter = new AgentMemoryLongMemEvalAdapter(
            Substitute.For<IMemoryService>(),
            Substitute.For<IChatClient>(),
            "typed-run",
            new LongMemEvalAdapterOptions
            {
                ExpandFactsByPredicate = expandFactsByPredicate || resolveQueryRelations,
                ResolveQueryRelations = resolveQueryRelations,
                GraphRagItems = graphRagItems
            });

        var act = () => adapter.InjectTimestampedConversationHistory(TwoTurnHistory());

        act.Should().Throw<InvalidOperationException>().WithMessage("*silently ignored*");
    }
}
