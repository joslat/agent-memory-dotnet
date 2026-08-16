using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 27.4. The derived query must actually reach retrieval, and the arm must void when it does not.
/// </summary>
/// <remarks>
/// <para>
/// The failure this guards is the one that voided six procedural-benefit runs: a treatment arm that
/// silently behaves like its control. If the rewriter's output never reaches <c>RecallRequest.Query</c>,
/// the run measures the control while reporting a treatment, and the result reads as a confident null.
/// </para>
/// </remarks>
public sealed class QueryFormulationWiringTests
{
    [Fact]
    public async Task TheDerivedQueryReachesRetrievalRatherThanTheOriginal()
    {
        // Red before 27.4: Query was always the prompt verbatim, so a formulator could run, spend a
        // model call per question, and change nothing about what was retrieved.
        var (memory, captured) = CreateMemory();
        var adapter = new AgentMemoryLongMemEvalAdapter(
            memory, Chat("ignored"), "test-run",
            new LongMemEvalAdapterOptions
            {
                QueryFormulator = new LongMemEvalQueryFormulator(
                    Chat("Zurich relocation March"), LongMemEvalQueryFormulation.Rewrite),
            });

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("Alice moved to Zurich.", "Noted.")]);
        await adapter.InvokeAsync("Where does Alice live?");

        captured.Query.Should().Be("Zurich relocation March");
    }

    [Fact]
    public async Task TheControlArmRetrievesWithTheQuestionVerbatim()
    {
        // The default must reproduce the historical path exactly, or every sealed measurement becomes
        // incomparable with anything taken after this change.
        var (memory, captured) = CreateMemory();
        var adapter = new AgentMemoryLongMemEvalAdapter(memory, Chat("ignored"), "test-run");

        await adapter.ResetSessionAsync();
        adapter.InjectConversationHistory([("Alice moved to Zurich.", "Noted.")]);
        await adapter.InvokeAsync("Where does Alice live?");

        captured.Query.Should().Be("Where does Alice live?");
    }

    [Fact]
    public async Task ARewriterThatChangesNothingVoidsTheArm()
    {
        // THE void witness. A rewriter echoing its input has measured the control; reporting "no
        // difference" would be a claim about a mechanism that never ran.
        var formulator = new LongMemEvalQueryFormulator(
            Echo(), LongMemEvalQueryFormulation.Rewrite);

        for (var i = 0; i < 10; i++)
            await formulator.DeriveAsync($"question {i}");

        formulator.Changed.Should().Be(0);
        formulator.VoidReason(10).Should().NotBeNull();
        formulator.VoidReason(10).Should().Contain("measured its own control");
    }

    [Fact]
    public async Task AFailingRewriterFallsBackButIsCountedRatherThanHidden()
    {
        // A silent fallback to the original question is exactly how an arm comes to measure its
        // control while believing otherwise. The fallback is correct; hiding it is not.
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChatResponse>>(_ => throw new InvalidOperationException("provider down"));

        var formulator = new LongMemEvalQueryFormulator(chat, LongMemEvalQueryFormulation.Rewrite);

        (await formulator.DeriveAsync("original")).Should().Be("original");
        formulator.Failed.Should().Be(1);
        formulator.VoidReason(1).Should().NotBeNull();
    }

    [Fact]
    public async Task AnEmptyRewriteIsTreatedAsAFailureNotAsAQuery()
    {
        // Retrieving on "" would return the corpus in arbitrary order and score as a catastrophic
        // retrieval regression caused by the harness rather than by the system under test.
        var formulator = new LongMemEvalQueryFormulator(Chat("   "), LongMemEvalQueryFormulation.Rewrite);

        (await formulator.DeriveAsync("original")).Should().Be("original");
        formulator.Failed.Should().Be(1);
    }

    [Fact]
    public async Task TheControlArmIsNeverVoid()
    {
        // Verbatim spends no model call and cannot fail, so it must never report a void reason --
        // otherwise every control run would be discarded.
        var formulator = new LongMemEvalQueryFormulator(Echo(), LongMemEvalQueryFormulation.Verbatim);

        (await formulator.DeriveAsync("q")).Should().Be("q");
        formulator.Derived.Should().Be(0);
        formulator.VoidReason(1).Should().BeNull();
    }

    [Fact]
    public async Task AnArmThatBarelyRanIsVoidEvenIfEveryQueryItTouchedChanged()
    {
        // THE gap the first real run exposed. changed/derived was 100% -- the formulator rewrote both
        // queries it saw -- while 48 of 50 questions never reached retrieval at all, and the witness
        // reported null. A witness satisfiable by a sample of two is not a witness.
        var formulator = new LongMemEvalQueryFormulator(Chat("rewritten"), LongMemEvalQueryFormulation.Rewrite);

        await formulator.DeriveAsync("q1");
        await formulator.DeriveAsync("q2");

        formulator.Changed.Should().Be(2);
        formulator.VoidReason(questionsAnswered: 50).Should().NotBeNull();
        formulator.VoidReason(questionsAnswered: 50).Should().Contain("ran on only 2 of 50");
        formulator.VoidReason(questionsAnswered: 2).Should().BeNull("it covered every question it had");
    }

    private static IChatClient Chat(string reply)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        return chat;
    }

    private static IChatClient Echo()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => new ChatResponse(new ChatMessage(
                ChatRole.Assistant, call.Arg<IEnumerable<ChatMessage>>().Last().Text ?? string.Empty)));
        return chat;
    }

    private sealed class Captured
    {
        public string? Query { get; set; }
    }

    private static (IMemoryService Memory, Captured Captured) CreateMemory()
    {
        var captured = new Captured();
        var memory = Substitute.For<IMemoryService>();
        memory.AddMessagesAsync(Arg.Any<IEnumerable<Message>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IEnumerable<Message>>().ToArray());
        memory.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<RecallRequest>();
                captured.Query = request.Query;
                return new RecallResult
                {
                    Context = new MemoryContext
                    {
                        SessionId = request.SessionId,
                        AssembledAtUtc = DateTimeOffset.UnixEpoch,
                        RelevantMessages = new MemoryContextSection<Message>
                        {
                            Items =
                            [
                                new Message
                                {
                                    MessageId = Guid.NewGuid().ToString("N"),
                                    SessionId = request.SessionId,
                                    ConversationId = request.SessionId,
                                    Role = "assistant",
                                    Content = "Alice moved to Zurich.",
                                    TimestampUtc = DateTimeOffset.UnixEpoch,
                                },
                            ],
                        },
                    },
                    TotalItemsRetrieved = 1,
                };
            });
        return (memory, captured);
    }
}
