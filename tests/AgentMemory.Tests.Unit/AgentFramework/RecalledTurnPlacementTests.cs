using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.AgentFramework.Security;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Recalled conversation turns are earlier conversation: they go before the live thread, in the order they
/// happened, and never repeat what the session's chat history already carries.
/// </summary>
/// <remarks>
/// MAF appends a context provider's messages after the request. Recalled turns returned newest first
/// therefore came after the user's new message, and the last user turn the model read was an old one: a
/// live agent answered the previous message ("Priya called me…" got "Priya Nair is already on file"). And
/// MAF hands the provider only the caller's new messages, so the recalled-history dedup could not see the
/// session history it was meant to deduplicate against.
/// </remarks>
public sealed class RecalledTurnPlacementTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private sealed class TestAgentSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Message Turn(string role, string content, int minute) => new()
    {
        MessageId = $"m-{minute}",
        ConversationId = "c1",
        SessionId = "s1",
        Role = role,
        Content = content,
        TimestampUtc = T0.AddMinutes(minute),
    };

    public RecalledTurnPlacementTests()
    {
        // Recent messages come back as recall orders them: newest first.
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = "s1",
                    AssembledAtUtc = T0,
                    RecentMessages = new MemoryContextSection<Message>
                    {
                        Items =
                        [
                            Turn("assistant", "Noted: Priya Shah has a dog.", 3),
                            Turn("user", "My neighbour Priya Shah has a dog.", 2),
                            Turn("assistant", "Got it: Priya Nair, billing.", 1),
                            Turn("user", "My colleague Priya Nair is on billing.", 0),
                        ],
                    },
                    RelevantFacts = new MemoryContextSection<Fact>
                    {
                        Items = [new Fact { FactId = "f1", Subject = "Priya Nair", Predicate = "works_on", Object = "billing", Confidence = 1, CreatedAtUtc = T0 }],
                    },
                },
            });
    }

    private Neo4jMemoryContextProvider Provider(ContextFormatOptions? format = null) =>
        new(
            _memoryService, Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IClock>(), Substitute.For<IIdGenerator>(),
            Options.Create(new MemoryOptions()),
            Options.Create(format ?? new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    private static AgentSession Session()
    {
        var session = new TestAgentSession();
        session.WithMemoryIdentity(userId: "alice", sessionId: "s1", conversationId: "c1");
        return session;
    }

    private static async Task<IReadOnlyList<ChatMessage>> InvokeAsync(AIContextProvider provider, IEnumerable<ChatMessage> request)
    {
#pragma warning disable MAAI001
        var context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(new StubAgent(), Session(), new AIContext { Messages = request }),
            CancellationToken.None);
#pragma warning restore MAAI001
        return context.Messages!.ToList();
    }

    private Task<IReadOnlyList<ChatMessage>> InvokeAsync(params ChatMessage[] request) => InvokeAsync(Provider(), request);

    /// <summary>The conversation as the model reads it: the framing line left out.</summary>
    private static List<string> Conversation(IEnumerable<ChatMessage> messages) =>
        messages.Where(m => m.Text != RecalledTurns.FramingText).Select(m => m.Text).ToList();

    private static ChatMessage External(ChatRole role, string text) =>
        new ChatMessage(role, text).WithAgentRequestMessageSource(AgentRequestMessageSourceType.External);

    private static ChatMessage History(ChatRole role, string text) =>
        new ChatMessage(role, text).WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);

    [Fact]
    public async Task The_users_new_message_is_the_last_user_turn_the_model_reads()
    {
        var messages = await InvokeAsync(External(ChatRole.User, "Priya called me, remind me about Friday."));

        messages.Last(m => m.Role == ChatRole.User).Text.Should().Be("Priya called me, remind me about Friday.");
    }

    [Fact]
    public async Task Recalled_turns_come_first_and_in_the_order_they_happened()
    {
        var messages = await InvokeAsync(External(ChatRole.User, "Priya called me."));
        messages[0].Text.Should().Be(RecalledTurns.FramingText, "recalled turns are framed as reference data (#92)");
        var texts = Conversation(messages);

        texts.Take(4).Should().Equal(
            "My colleague Priya Nair is on billing.",
            "Got it: Priya Nair, billing.",
            "My neighbour Priya Shah has a dog.",
            "Noted: Priya Shah has a dog.");
        texts.IndexOf("Priya called me.").Should().Be(4);
        // Memory blocks stay where they were: after the request.
        texts.FindIndex(t => t.Contains("<recalled_memory")).Should().BeGreaterThan(4);
    }

    [Fact]
    public async Task A_host_system_prompt_stays_first()
    {
        var messages = await InvokeAsync(
            External(ChatRole.System, "You are a helpful friend."),
            External(ChatRole.User, "Priya called me."));

        messages[0].Text.Should().Be("You are a helpful friend.");
        messages[1].Text.Should().Be(RecalledTurns.FramingText);
        messages[2].Text.Should().Be("My colleague Priya Nair is on billing.");
    }

    [Fact]
    public async Task Turns_the_session_history_already_carries_are_not_recalled_again()
    {
        // The session's history holds the two newest turns; recall returns those and two older ones.
        var messages = await InvokeAsync(
            History(ChatRole.User, "My neighbour Priya Shah has a dog."),
            History(ChatRole.Assistant, "Noted: Priya Shah has a dog."),
            External(ChatRole.User, "Priya called me."));
        var texts = Conversation(messages);

        texts.Count(t => t == "My neighbour Priya Shah has a dog.").Should().Be(1, "the history copy only");
        texts.Count(t => t == "Noted: Priya Shah has a dog.").Should().Be(1);
        texts.Take(2).Should().Equal("My colleague Priya Nair is on billing.", "Got it: Priya Nair, billing.");
        texts.IndexOf("My neighbour Priya Shah has a dog.").Should().Be(2, "the older recalled turns precede the history");
        messages.Last(m => m.Role == ChatRole.User).Text.Should().Be("Priya called me.");
    }

    [Fact]
    public async Task The_mark_does_not_leave_the_provider()
    {
        var messages = await InvokeAsync(External(ChatRole.User, "Priya called me."));

        messages.Should().NotContain(m => m.AdditionalProperties != null && m.AdditionalProperties.ContainsKey(RecalledTurns.Property),
            "the mark would otherwise be stored in MAF's history and serialised with the session");
    }

    [Fact]
    public async Task A_history_that_kept_last_turns_injected_messages_is_left_where_it_is()
    {
        // MAF's default history stores injected messages. Replayed on the next turn they are ChatHistory-
        // sourced: not this turn's recall, so they must not be moved (only this provider's own output is).
        var first = await InvokeAsync(External(ChatRole.User, "Priya called me."));
        var replayed = first.Select(m => History(m.Role, m.Text!)).ToList();

        var second = await InvokeAsync(replayed.Append(External(ChatRole.User, "And Friday?")).ToArray());
        var historyPositions = replayed.Select(h => second.ToList().FindIndex(m => ReferenceEquals(m, h))).ToList();

        historyPositions.Should().BeInAscendingOrder().And.NotContain(-1);
        second.Last(m => m.Role == ChatRole.User).Text.Should().Be("And Friday?");
    }

    [Fact]
    public async Task With_memory_at_the_user_role_the_question_is_still_the_last_user_message()
    {
        // Hardened hosts render memory blocks at the user role (#92): left after the request, the last user
        // message the model read was a <recalled_memory> block, not the question.
        var provider = Provider(new ContextFormatOptions { DefaultMemoryRole = RecalledMemoryMessageRole.User });

        var messages = await InvokeAsync(provider, [External(ChatRole.User, "Priya called me.")]);

        messages.Last(m => m.Role == ChatRole.User).Text.Should().Be("Priya called me.");
        messages.Should().Contain(m => m.Text!.Contains("<recalled_memory"), "the blocks are still there, before the question");
    }

    [Fact]
    public async Task A_lazily_enumerated_request_is_read_once_and_the_callers_context_is_untouched()
    {
        int enumerations = 0;
        IEnumerable<ChatMessage> Lazy()
        {
            enumerations++;
            yield return External(ChatRole.User, "Priya called me.");
        }
        var callerContext = new AIContext { Messages = Lazy() };
        var original = callerContext.Messages;

#pragma warning disable MAAI001
        var result = await Provider().InvokingAsync(
            new AIContextProvider.InvokingContext(new StubAgent(), Session(), callerContext), CancellationToken.None);
#pragma warning restore MAAI001

        enumerations.Should().Be(1);
        callerContext.Messages.Should().BeSameAs(original);
        result.Messages!.Last(m => m.Role == ChatRole.User).Text.Should().Be("Priya called me.");
    }

    [Fact]
    public void The_budget_keeps_the_newest_turns_and_emits_them_oldest_first()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = T0,
            RecentMessages = new MemoryContextSection<Message>
            {
                Items = [Turn("user", "fourth", 3), Turn("user", "third", 2), Turn("user", "second", 1), Turn("user", "first", 0)],
            },
        };

        var texts = Conversation(AgentMemory.AgentFramework.Mapping.MafTypeMapper.ToContextMessages(
            context, new ContextFormatOptions { MaxChatHistoryMessages = 2, ContextPrefix = "" }));

        texts.Should().Equal("third", "fourth");
    }

    [Fact]
    public void A_reply_never_precedes_the_message_it_answers_when_timestamps_tie()
    {
        // Recall is newest first: the reply before the question. Equal timestamps (a fixed clock, a replay)
        // must still come out question first.
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = T0,
            RecentMessages = new MemoryContextSection<Message>
            {
                Items = [Turn("assistant", "the answer", 5) with { MessageId = "a" }, Turn("user", "the question", 5) with { MessageId = "q" }],
            },
        };

        var texts = Conversation(AgentMemory.AgentFramework.Mapping.MafTypeMapper.ToContextMessages(
            context, new ContextFormatOptions { ContextPrefix = "" }));

        texts.Should().Equal("the question", "the answer");
    }

    [Fact]
    public async Task A_hardened_host_never_puts_memory_between_a_tool_call_and_its_result()
    {
        // A user message between an assistant tool call and its tool result makes the request invalid
        // (OpenAI-style APIs answer 400). The memory anchors on the caller's last USER message instead.
        var provider = Provider(new ContextFormatOptions { DefaultMemoryRole = RecalledMemoryMessageRole.User });
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup")])
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "done")])
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.External);

        var messages = (await InvokeAsync(provider, [External(ChatRole.User, "Priya called me."), call, result])).ToList();

        messages.IndexOf(result).Should().Be(messages.IndexOf(call) + 1, "the tool result must follow its call directly");
        var question = messages.FindIndex(m => m.Text == "Priya called me.");
        messages.Skip(question + 1).Should().NotContain(m => m.Text.Contains("<recalled_memory"),
            "the memory goes before the question, not into the tool exchange");
    }

    [Fact]
    public async Task A_hardened_host_keeps_the_prefix_with_the_blocks_it_introduces()
    {
        var provider = Provider(new ContextFormatOptions { DefaultMemoryRole = RecalledMemoryMessageRole.User });

        var messages = (await InvokeAsync(provider, [External(ChatRole.User, "Priya called me.")])).ToList();

        var prefix = messages.FindIndex(m => m.Text.StartsWith("The following is recalled memory context", StringComparison.Ordinal));
        var block = messages.FindIndex(m => m.Text.StartsWith("<recalled_memory", StringComparison.Ordinal));
        var question = messages.FindIndex(m => m.Text == "Priya called me.");
        prefix.Should().BeLessThan(block);
        block.Should().BeLessThan(question);
    }

    [Fact]
    public async Task The_facade_emits_recalled_turns_in_time_order_and_unmarked()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = T0,
                RecentMessages = new MemoryContextSection<Message>
                {
                    // Recall order: newest first. Twelve turns, a chat budget of ten.
                    Items = Enumerable.Range(0, 12).Reverse().Select(i => Turn("user", $"turn {i}", i)).ToList(),
                },
            },
        });
        var facade = new Neo4jMicrosoftMemoryFacade(
            memory, new Neo4jChatMessageStore(memory, Substitute.For<IClock>(), Substitute.For<IIdGenerator>(), NullLogger<Neo4jChatMessageStore>.Instance),
            Options.Create(new AgentFrameworkOptions()), NullLogger<Neo4jMicrosoftMemoryFacade>.Instance);

        var result = await facade.GetContextForRunAsync([], "s1", "c1");
        var turns = Conversation(result).Where(t => t.StartsWith("turn ", StringComparison.Ordinal)).ToList();

        // The facade sizes its own chat budget to what recall returned: every turn, in the order it happened
        // (it reversed the recents itself before, which the mapper's budget then cut from the wrong end).
        turns.Should().Equal(Enumerable.Range(0, 12).Select(i => $"turn {i}"));
        result.Should().NotContain(m => m.AdditionalProperties != null && m.AdditionalProperties.ContainsKey(RecalledTurns.Property));
    }

    // ── NAMS: the same rule, the same helper ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_NAMS_provider_places_recalled_turns_before_the_new_message_too()
    {
        var recall = Substitute.For<AgentMemory.Nams.Recall.INamsRecallService>();
        recall.RecallAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AgentMemory.Nams.Recall.NamsRecallResult
            {
                // NAMS recall is newest first.
                Items =
                [
                    NamsTurn("Noted: Priya Shah has a dog.", "assistant", "2026-09-26T10:03:00Z"),
                    NamsTurn("My neighbour Priya Shah has a dog.", "user", "2026-09-26T10:02:00Z"),
                    NamsTurn("My colleague Priya Nair is on billing.", "user", "2026-09-26T10:00:00Z"),
                ],
            });
        var resolver = Substitute.For<AgentMemory.Nams.Identity.INamsConversationResolver>();
        resolver.ResolveAsync(Arg.Any<AgentMemory.Nams.Identity.NamsConversationIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new AgentMemory.Nams.Identity.NamsConversationResolutionResult("conv-1", WasCreated: false));
        var provider = new AgentMemory.AgentFramework.Nams.NamsMemoryContextProvider(
            resolver, recall, Substitute.For<AgentMemory.Nams.Persistence.INamsPersistenceService>(),
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<AgentMemory.AgentFramework.Nams.NamsMemoryContextProvider>.Instance);

        var messages = await InvokeAsync(provider, [External(ChatRole.User, "Priya called me.")]);
        var texts = Conversation(messages);

        texts.Take(3).Should().Equal("My colleague Priya Nair is on billing.", "My neighbour Priya Shah has a dog.", "Noted: Priya Shah has a dog.");
        messages.Last(m => m.Role == ChatRole.User).Text.Should().Be("Priya called me.");
    }

    [Fact]
    public void NAMS_turns_follow_their_createdAt_whatever_order_the_server_returns()
    {
        var recall = new AgentMemory.Nams.Recall.NamsRecallResult
        {
            // Oldest first this time, and one more than the budget of two.
            Items =
            [
                NamsTurn("first", "user", "2026-09-26T10:00:00Z"),
                NamsTurn("second", "assistant", "2026-09-26T10:01:00Z"),
                NamsTurn("third", "user", "2026-09-26T10:02:00Z"),
            ],
        };

        var texts = Conversation(AgentMemory.AgentFramework.Nams.Mapping.NamsMafTypeMapper.ToContextMessages(
            recall, new ContextFormatOptions { MaxChatHistoryMessages = 2, ContextPrefix = "" },
            new AgentMemory.AgentFramework.Security.DefaultMemoryContextAdmissionPolicy()));

        texts.Should().Equal("second", "third");
    }

    private static AgentMemory.Nams.Recall.NamsRecalledItem NamsTurn(string content, string role, string? createdAt = null) => new()
    {
        CreatedAt = createdAt,
        SourceId = Guid.NewGuid().ToString("N"),
        Category = AgentMemory.Nams.Recall.NamsRecallCategory.RecentMessage,
        Content = content,
        Provenance = AgentMemory.Nams.Recall.NamsRecallProvenance.UserProvided,
        Role = role,
    };
}
