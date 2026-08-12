using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The wiring half of E2: the extraction window has to be filled in, once, where every host passes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentMemory.Abstractions.Domain.ExtractionWindow"/> and its rendering have their own
/// tests, and none of them prove a single context turn ever reaches an extractor. The window would be
/// correct, the prompt instruction would be authored, and every extraction would still see one batch.
/// </para>
/// <para>
/// It is filled at <c>ExtractAndPersistAsync</c> rather than per caller because the Agent Framework
/// provider, the Microsoft memory facade and the MCP ingest tool all arrive there. Resolving it per
/// caller would make the window depend on the host — a setting only some components respect, which is
/// the failure this codebase has hit repeatedly.
/// </para>
/// </remarks>
public sealed class MemoryServiceExtractionContextTests
{
    private readonly IShortTermMemoryService _shortTerm = Substitute.For<IShortTermMemoryService>();
    private readonly IMemoryExtractionPipeline _pipeline = Substitute.For<IMemoryExtractionPipeline>();

    private static Message M(string id, string content) => new()
    {
        MessageId = id,
        ConversationId = "c-1",
        SessionId = "s-1",
        Role = "user",
        Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    public MemoryServiceExtractionContextTests()
    {
        _pipeline.ExtractAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult()));
    }

    private MemoryService CreateSut(int contextTurns) =>
        new(_shortTerm,
            Substitute.For<IMemoryContextAssembler>(),
            _pipeline,
            Substitute.For<IEntityRepository>(),
            Substitute.For<IFactRepository>(),
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new MemoryOptions
            {
                Extraction = new ExtractionOptions { ExtractionContextTurns = contextTurns },
            }),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryService>.Instance);

    private void SessionHolds(params Message[] messages) =>
        _shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(messages));

    private ExtractionRequest CapturedRequest() =>
        (ExtractionRequest)_pipeline.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IMemoryExtractionPipeline.ExtractAsync))
            .GetArguments()[0]!;

    private Task ExtractAsync(int contextTurns, params Message[] targets) =>
        CreateSut(contextTurns).ExtractAndPersistAsync(
            new ExtractionRequest { Messages = targets, SessionId = "s-1" });

    [Fact]
    public async Task PrecedingTurnsAreAttachedAsContext()
    {
        // THE test. Everything else in E2 is arrangement around these turns reaching the extractor.
        SessionHolds(M("m-1", "Zurich is lovely"), M("m-2", "Agreed."), M("m-3", "I moved there"));

        await ExtractAsync(2, M("m-3", "I moved there"));

        CapturedRequest().ContextMessages.Select(m => m.MessageId).Should().Equal("m-1", "m-2");
    }

    [Fact]
    public async Task TheBatchItselfIsNeverItsOwnContext()
    {
        // The recency query returns the batch too -- it was just persisted. Leaving it in would hand
        // the model the same turn twice, the second copy labelled "do not extract from this".
        SessionHolds(M("m-1", "earlier"), M("m-2", "target"));

        await ExtractAsync(4, M("m-2", "target"));

        CapturedRequest().ContextMessages.Select(m => m.MessageId).Should().Equal("m-1");
    }

    [Fact]
    public async Task OnlyTheMostRecentTurnsAreKept()
    {
        SessionHolds(M("m-1", "oldest"), M("m-2", "older"), M("m-3", "old"), M("m-4", "target"));

        await ExtractAsync(2, M("m-4", "target"));

        // Note the collection overload: Equal("m-2", "m-3", "because...") would fold the reason into
        // the expected array as a third element.
        CapturedRequest().ContextMessages.Select(m => m.MessageId).Should().Equal(
            ["m-2", "m-3"],
            "context is the turns immediately before the batch, not the start of the session");
    }

    [Fact]
    public async Task ZeroTurnsQueriesNothingAtAll()
    {
        // The off state, and it must be off at the source: a fetch whose result is then discarded
        // still costs a round trip on every single extraction.
        await ExtractAsync(0, M("m-2", "target"));

        await _shortTerm.DidNotReceiveWithAnyArgs()
            .GetRecentMessagesAsync(default!, default, default);
        CapturedRequest().ContextMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task AnExplicitContextIsNotOverwritten()
    {
        // A caller that knows the conversation better than a recency query does should not be
        // second-guessed.
        SessionHolds(M("m-1", "from the store"), M("m-9", "target"));

        await CreateSut(4).ExtractAndPersistAsync(new ExtractionRequest
        {
            Messages = [M("m-9", "target")],
            SessionId = "s-1",
            ContextMessages = [M("m-7", "chosen by the caller")],
        });

        CapturedRequest().ContextMessages.Select(m => m.MessageId).Should().Equal("m-7");
    }

    [Fact]
    public async Task NoEarlierTurnsLeavesTheRequestUntouched()
    {
        // First turn of a session: nothing precedes it, and an empty context must stay empty rather
        // than becoming a fenced block with nothing inside it.
        SessionHolds(M("m-1", "target"));

        await ExtractAsync(4, M("m-1", "target"));

        CapturedRequest().ContextMessages.Should().BeEmpty();
    }
}
