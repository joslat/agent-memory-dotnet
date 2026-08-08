using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G5. Guards that the option reaches the actual recall request. Twice today a mechanism shipped
/// green — BuildSystemPrompt and the fused write path — while nothing called it, and only a live run
/// exposed it. This asserts the call site, not the capability.
/// </summary>
public sealed class LongMemEvalExpansionWiringTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheExpansionOptionReachesTheRecallRequest(bool expand)
    {
        var memory = Substitute.For<IMemoryService>();
        RecallRequest? captured = null;
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<RecallRequest>();
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = captured.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items = [new Message
                            {
                                MessageId = "m",
                                SessionId = captured.SessionId,
                                ConversationId = captured.SessionId,
                                Role = "user",
                                Content = "recalled",
                                TimestampUtc = DateTimeOffset.UnixEpoch
                            }]
                        }
                    },
                    TotalItemsRetrieved = 1
                };
            });

        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer")));

        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, chat, "expansion-run",
            new LongMemEvalAdapterOptions
            {
                ExpandFactsByPredicate = expand,
                MaxExpandedFacts = 50
            });
        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("a question was asked", "an answer was given")]);
        await adapter.InvokeAsync("What happened?");

        captured.Should().NotBeNull();
        captured!.Options.ExpandFactsByPredicate.Should().Be(expand);
        captured.Options.MaxExpandedFacts.Should().Be(50);
    }
}
