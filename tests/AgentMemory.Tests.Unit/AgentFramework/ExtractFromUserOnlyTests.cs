using AgentMemory.Tests.Unit.TestSupport;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// D7: a fact the agent repeats back is not learned again. With the reply extracted too, every repetition
/// raised the fact's mention count, and the (default-on) profile tier ranks by mentions.
/// </summary>
public sealed class ExtractFromUserOnlyTests
{
    private sealed class TestAgentSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session,
            AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(System.Text.Json.JsonElement serializedState,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(AgentSession session,
            System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task<ExtractionRequest?> ExtractedAsync(bool userOnly)
    {
        var memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();
        ExtractionRequest? seen = null;
        memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult { SourceMessageIds = [] }));
        memory.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new Message
            {
                MessageId = Guid.NewGuid().ToString("N"), SessionId = call.ArgAt<string>(0), ConversationId = call.ArgAt<string>(1),
                Role = call.ArgAt<string>(2), Content = call.ArgAt<string>(3), TimestampUtc = DateTimeOffset.UnixEpoch,
            }));
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var provider = new Neo4jMemoryContextProvider(
            memory, Substitute.For<IEmbeddingOrchestrator>(), Substitute.For<IClock>(), ids,
            Options.Create(new MemoryOptions()), Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions { ExtractFromUserMessagesOnly = userOnly }),
            NullLogger<Neo4jMemoryContextProvider>.Instance);
        var session = new TestAgentSession();
        session.WithMemoryIdentity(userId: "alice", sessionId: "s1", conversationId: "c1");

#pragma warning disable MAAI001
        await provider.InvokedAsync(new AIContextProvider.InvokedContext(new StubAgent(), session,
            [new ChatMessage(ChatRole.User, "I just moved to Porto.")],
            [new ChatMessage(ChatRole.Assistant, "Porto! And you still work at Northwind, right?")]),
            CancellationToken.None);
#pragma warning restore MAAI001
        return seen;
    }

    [Fact]
    public async Task On_only_the_users_words_are_extracted()
    {
        var request = await ExtractedAsync(userOnly: true);

        request!.Messages.Select(m => m.Content).Should().Equal("I just moved to Porto.");
    }

    [Fact]
    public async Task Off_the_reply_is_extracted_too_as_before()
    {
        var request = await ExtractedAsync(userOnly: false);

        request!.Messages.Should().HaveCount(2);
    }

    // Review round 3: the chat-history provider and the facade ignored the flag. All three now extract
    // through TurnExtraction, which applies it once.

    private static Message M(string role, string content) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"), SessionId = "s1", ConversationId = "c1", Role = role, Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task The_shared_dispatcher_keeps_only_what_the_user_said()
    {
        var memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();
        ExtractionRequest? seen = null;
        memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult { SourceMessageIds = [] }));

        await TurnExtraction.ExtractAsync(memory,
            new ExtractionRequest { Messages = [M("user", "I moved to Porto."), M("assistant", "You work at Northwind, right?")], SessionId = "s1" },
            new AgentFrameworkOptions { ExtractFromUserMessagesOnly = true }, null, NullLogger.Instance, CancellationToken.None);

        seen!.Messages.Select(m => m.Content).Should().Equal("I moved to Porto.");
    }

    [Fact]
    public async Task A_turn_with_nothing_the_user_said_extracts_nothing()
    {
        var memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();

        await TurnExtraction.ExtractAsync(memory,
            new ExtractionRequest { Messages = [M("assistant", "Here is a summary of your week.")], SessionId = "s1" },
            new AgentFrameworkOptions { ExtractFromUserMessagesOnly = true }, null, NullLogger.Instance, CancellationToken.None);

        await memory.DidNotReceiveWithAnyArgs().ExtractAndPersistAsync(default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_host_says_a_question_turn_may_wait_only_when_asked(bool defer)
    {
        var memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();
        ExtractionRequest? seen = null;
        memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult { SourceMessageIds = [] }));

        await TurnExtraction.ExtractAsync(memory,
            new ExtractionRequest { Messages = [M("user", "What's a good hike?")], SessionId = "s1" },
            new AgentFrameworkOptions { DeferQuestionTurns = defer }, null, NullLogger.Instance, CancellationToken.None);

        seen!.DeferIfOnlyAsking.Should().Be(defer);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recommended_migration_dispatches_separate_user_turns_and_honours_a_later_deferral_override(
        bool queued, bool overrideDeferral)
    {
        var memory = Substitute.For<IMemoryService>();
        var seen = new List<ExtractionRequest>();
        memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(seen.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult { SourceMessageIds = [] }));
        var work = new List<Func<CancellationToken, Task>>();
        var background = Substitute.For<IBackgroundExtraction>();
        background.TryEnqueue(Arg.Any<string>(), Arg.Do<Func<CancellationToken, Task>>(work.Add)).Returns(true);
        var options = new AgentFrameworkOptions().ApplyConversational().ApplyRecommended();
        options.ExtractInBackground = queued;
        if (overrideDeferral) options.DeferQuestionTurns = true;

        foreach (var text in new[] { "What's a good hike?", "I moved to Porto." })
        {
            await TurnExtraction.ExtractAsync(memory,
                new ExtractionRequest
                {
                    Messages = [M("user", text), M("assistant", "Try the coast.")],
                    SessionId = "s1", UserId = "alice",
                }, options, background, NullLogger.Instance, CancellationToken.None);
        }

        if (queued)
        {
            seen.Should().BeEmpty("queued work must not run inline");
            work.Should().HaveCount(2);
            foreach (var run in work) await run(CancellationToken.None);
        }
        else work.Should().BeEmpty();

        seen.Should().HaveCount(2);
        seen.Select(r => r.Messages.Single().Content).Should().Equal("What's a good hike?", "I moved to Porto.");
        seen.Should().OnlyContain(r => r.DeferIfOnlyAsking == overrideDeferral && r.UserId == "alice" && r.SessionId == "s1");
    }
}
