using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 37.4: a turn that only asks is held on its stored message and extracted with the next turn of the session that
/// tells something. Durable by construction: the mark lives in the store (the fake below plays it).
/// </summary>
public sealed class DeferredQuestionTurnsTests
{
    private readonly List<Message> _stored = [];
    private readonly Dictionary<string, string> _deferred = [];
    private readonly List<ExtractionRequest> _extracted = [];
    private readonly IMessageRepository _messages = Substitute.For<IMessageRepository>();
    private readonly IMemoryExtractionPipeline _pipeline = Substitute.For<IMemoryExtractionPipeline>();
    private IngestionStatus _status = IngestionStatus.Succeeded;
    private int _clock;

    public DeferredQuestionTurnsTests()
    {
        _messages.SetExtractionDeferredAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                foreach (var id in ci.ArgAt<IReadOnlyCollection<string>>(0))
                    if (ci.ArgAt<string?>(1) is { } heldFor) _deferred[id] = heldFor; else _deferred.Remove(id);
                return Task.CompletedTask;
            });
        _messages.GetExtractionDeferredAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Message>>(_stored
                .Where(m => _deferred.TryGetValue(m.MessageId, out var heldFor) && heldFor == ci.ArgAt<string>(0))
                .OrderBy(m => m.TimestampUtc).Take(ci.ArgAt<int>(1)).ToList()));
        _pipeline.ExtractAsync(Arg.Do<ExtractionRequest>(_extracted.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ExtractionResult { Status = _status }));
    }

    private MemoryService Sut(int maxDeferred = 3, IMessageRepository? messages = null)
    {
        var options = new MemoryOptions();
        options.Extraction.MaxDeferredTurns = maxDeferred;
        return new MemoryService(
            Substitute.For<IShortTermMemoryService>(), Substitute.For<IMemoryContextAssembler>(), _pipeline,
            Substitute.For<IEntityRepository>(), Substitute.For<IFactRepository>(), Substitute.For<IPreferenceRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(), Microsoft.Extensions.Options.Options.Create(options),
            Substitute.For<IClock>(), Substitute.For<IIdGenerator>(), NullLogger<MemoryService>.Instance,
            messageRepository: messages ?? _messages);
    }

    private ExtractionRequest Turn(string content, string session = "s1", bool defer = true, string? owner = null)
    {
        var message = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"), SessionId = session, ConversationId = "c", Role = "user",
            Content = content, TimestampUtc = DateTimeOffset.UnixEpoch.AddMinutes(++_clock),
        };
        _stored.Add(message);
        return new ExtractionRequest { Messages = [message], SessionId = session, DeferIfOnlyAsking = defer, UserId = owner };
    }

    private static IEnumerable<string> Said(ExtractionRequest request) => request.Messages.Select(m => m.Content);

    [Fact]
    public async Task A_question_waits_and_the_next_telling_turn_takes_it_along()
    {
        var sut = Sut();

        var held = await sut.ExtractAndPersistAsync(Turn("What should I get my sister Ana, who loves pottery?"));
        _extracted.Should().BeEmpty();
        held.Metadata.Should().ContainKey(MemoryService.DeferredMetadataKey);

        await sut.ExtractAndPersistAsync(Turn("I found a glazing class in Porto."));

        Said(_extracted.Single()).Should().Equal("What should I get my sister Ana, who loves pottery?", "I found a glazing class in Porto.");
        _deferred.Should().BeEmpty("extracted, they no longer wait");
    }

    [Fact]
    public async Task A_failed_extraction_leaves_them_waiting()
    {
        var sut = Sut();
        await sut.ExtractAndPersistAsync(Turn("What's a good hike?"));
        _status = IngestionStatus.Failed;

        await sut.ExtractAndPersistAsync(Turn("I moved to Porto."));

        _deferred.Should().ContainSingle("nothing is lost when an extraction fails");
    }

    [Fact]
    public async Task No_more_than_the_maximum_wait()
    {
        var sut = Sut(maxDeferred: 2);
        await sut.ExtractAndPersistAsync(Turn("What's a good hike?"));
        await sut.ExtractAndPersistAsync(Turn("Is it going to rain?"));

        Said(_extracted.Single()).Should().Equal("What's a good hike?", "Is it going to rain?");
    }

    [Fact]
    public async Task One_session_s_questions_never_join_another_session()
    {
        var sut = Sut();
        await sut.ExtractAndPersistAsync(Turn("What's a good hike?", session: "s1"));

        await sut.ExtractAndPersistAsync(Turn("I moved to Porto.", session: "s2"));

        Said(_extracted.Single()).Should().Equal("I moved to Porto.");
    }

    // Review round 1 (M1): hosts that give every user of an agent one session id must never release one owner's words
    // into another owner's extraction.
    [Fact]
    public async Task One_owner_s_questions_never_join_another_owner_s_extraction_in_a_shared_session()
    {
        var sut = Sut();
        await sut.ExtractAndPersistAsync(Turn("What should I get my sister?", session: "agent", owner: "alice"));

        await sut.ExtractAndPersistAsync(Turn("I moved to Porto.", session: "agent", owner: "bob"));

        Said(_extracted.Single()).Should().Equal("I moved to Porto.");
        _extracted.Single().UserId.Should().Be("bob");
    }

    // Review round 1 (M3): a session that ends on a question is taken along by the owner's next telling turn.
    [Fact]
    public async Task A_question_that_ended_a_session_waits_for_the_owner_s_next_session()
    {
        var sut = Sut();
        await sut.ExtractAndPersistAsync(Turn("What's a good hike?", session: "monday", owner: "alice"));

        await sut.ExtractAndPersistAsync(Turn("I moved to Porto.", session: "tuesday", owner: "alice"));

        Said(_extracted.Single()).Should().Equal("What's a good hike?", "I moved to Porto.");
    }

    // Review round 1 (M2): a store failing now does not lose the turn.
    [Fact]
    public async Task A_failing_store_extracts_the_turn_now()
    {
        var failing = Substitute.For<IMessageRepository>();
        failing.GetExtractionDeferredAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transient"));

        await Sut(messages: failing).ExtractAndPersistAsync(Turn("What's a good hike?"));

        _extracted.Should().ContainSingle();
    }

    [Theory]
    [InlineData("I moved to Porto.")]
    [InlineData("Can you remember that my sister is Ana?")]
    [InlineData("Please call me Jose.")]
    [InlineData("Help me plan my wedding in June.")]
    public async Task A_turn_that_tells_something_is_extracted_now(string content)
    {
        await Sut().ExtractAndPersistAsync(Turn(content));

        _extracted.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_the_request_s_consent_every_turn_is_extracted()
    {
        await Sut().ExtractAndPersistAsync(Turn("What's a good hike?", defer: false));

        _extracted.Should().ContainSingle();
    }

    [Fact]
    public async Task A_store_that_cannot_hold_a_turn_extracts_it_now()
    {
        var plain = Substitute.For<IMessageRepository>();
        plain.GetExtractionDeferredAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new NotSupportedException());

        await Sut(messages: plain).ExtractAndPersistAsync(Turn("What's a good hike?"));

        _extracted.Should().ContainSingle();
    }
}
