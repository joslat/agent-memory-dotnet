using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 27.2. The answer seed must actually reach the answer call, and must be absent unless asked for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test exists.</b> The defect this project keeps rediscovering is a feature that ships,
/// compiles, has tests, and is reachable by nothing — found roughly thirteen times in this track
/// alone. <c>AnswerSeed</c> is exactly that shape: an option on a record, read in one place, whose
/// failure mode is silence. Without this test a typo in <c>Program.cs</c> would leave every run
/// unseeded while the report cheerfully printed <c>seeded-12345-temperature-1</c>.
/// </para>
/// <para>
/// <b>Both directions are asserted because both can break independently.</b> A test that only checked
/// the seeded case would pass on a build that seeded unconditionally — which would silently make every
/// new run incomparable with the entire sealed archive, all of which was measured unseeded.
/// </para>
/// <para>
/// <b>Temperature is asserted to be null, deliberately.</b> This deployment refuses every value but
/// its own default: <c>temperature: 0</c> returns HTTP 400 <i>"does not support 0 with this model.
/// Only the default (1) value is supported"</i>. Setting temperature here would not pin the model, it
/// would fail the run.
/// </para>
/// </remarks>
public sealed class AnswerSeedWiringTests
{
    [Fact]
    public async Task AnswerSeed_WhenSet_ReachesTheAnswerCall()
    {
        var (chat, captured) = CreateChat();
        var adapter = new AgentMemoryLongMemEvalAdapter(
            CreateMemory(), chat, "test-run",
            new LongMemEvalAdapterOptions { AnswerSeed = 4242 });

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("Alice moved to Zurich.", "Noted.")]);
        await adapter.InvokeAsync("Where does Alice live?");

        captured.Options.Should().NotBeNull("the seed is useless if no ChatOptions is sent");
        captured.Options!.Seed.Should().Be(4242);
        captured.Options.Temperature.Should().BeNull(
            "this deployment rejects every temperature but its default, so sending one fails the run");
    }

    [Fact]
    public async Task AnswerSeed_WhenUnset_SendsNoChatOptionsAtAll()
    {
        // The historical call passed no options object. Every sealed measurement in the archive was
        // taken that way, so the default must reproduce it byte for byte rather than sending an
        // empty-but-present ChatOptions.
        var (chat, captured) = CreateChat();
        var adapter = new AgentMemoryLongMemEvalAdapter(CreateMemory(), chat, "test-run");

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("Alice moved to Zurich.", "Noted.")]);
        await adapter.InvokeAsync("Where does Alice live?");

        captured.Options.Should().BeNull();
    }

    private sealed class Captured
    {
        public ChatOptions? Options { get; set; }
    }

    private static (IChatClient Chat, Captured Captured) CreateChat()
    {
        var captured = new Captured();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured.Options = call.Arg<ChatOptions?>();
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Zurich."));
            });
        return (chat, captured);
    }

    private static IMemoryService CreateMemory()
    {
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            // Must return something: the adapter refuses to answer on an empty recall rather than
            // manufacture a score, and that guard fires before the answer call this test inspects.
            .Returns(call => new RecallResult
            {
                Context = new MemoryContext
                {
                    SessionId = call.Arg<RecallRequest>().SessionId,
                    AssembledAtUtc = DateTimeOffset.UnixEpoch,
                    RelevantMessages = new MemoryContextSection<Message>
                    {
                        Items =
                        [
                            new Message
                            {
                                MessageId = Guid.NewGuid().ToString("N"),
                                SessionId = call.Arg<RecallRequest>().SessionId,
                                ConversationId = call.Arg<RecallRequest>().SessionId,
                                Role = "assistant",
                                Content = "Alice moved to Zurich.",
                                TimestampUtc = DateTimeOffset.UnixEpoch,
                            },
                        ],
                    },
                },
                TotalItemsRetrieved = 1,
            });
        return memory;
    }
}
