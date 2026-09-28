using AgentMemory.Tests.Unit.TestSupport;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// 36.5: a turn in which the user only asks is not extracted on its own (43 % of extraction calls returned nothing,
/// almost all on question turns); it is extracted with the next turn that tells something, so nothing is lost.
/// </summary>
public sealed class DeferQuestionTurnsTests
{
    private static Message User(string content) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"), SessionId = "s1", ConversationId = "c1", Role = "user", Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    private readonly List<ExtractionRequest> _extracted = [];
    private readonly IMemoryService _memory;

    public DeferQuestionTurnsTests()
    {
        _memory = Substitute.For<IMemoryService>().RouteIdKeyedAdds();
        _memory.ExtractAndPersistAsync(Arg.Do<ExtractionRequest>(_extracted.Add), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExtractionResult { SourceMessageIds = [] }));
    }

    private Task SayAsync(AgentFrameworkOptions options, string content, string owner = "u1") =>
        TurnExtraction.ExtractAsync(_memory, new ExtractionRequest { Messages = [User(content)], SessionId = "s1", UserId = owner },
            options, null, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task A_question_waits_and_is_extracted_with_the_next_turn_that_tells_something()
    {
        var options = new AgentFrameworkOptions { DeferQuestionTurns = true };

        await SayAsync(options, "What should I get my sister Ana, who loves pottery?");
        _extracted.Should().BeEmpty();

        await SayAsync(options, "I found a glazing class in Porto.");

        _extracted.Should().ContainSingle().Which.Messages.Select(m => m.Content).Should().Equal(
            "What should I get my sister Ana, who loves pottery?", "I found a glazing class in Porto.");
    }

    [Fact]
    public async Task No_more_than_the_maximum_wait()
    {
        var options = new AgentFrameworkOptions { DeferQuestionTurns = true, MaxDeferredTurns = 2 };

        await SayAsync(options, "What's a good hike?");
        await SayAsync(options, "Can you recommend a book?");
        _extracted.Should().BeEmpty();
        await SayAsync(options, "Is it going to rain?");

        _extracted.Should().ContainSingle().Which.Messages.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("I moved to Porto.")]
    [InlineData("Who was with me in Sintra? I went with Pedro.")]
    [InlineData("Can you remember that my sister is Ana?")]
    [InlineData("Please remind me to call her on Friday.")]
    public async Task A_turn_that_tells_something_is_extracted_now(string content)
    {
        await SayAsync(new AgentFrameworkOptions { DeferQuestionTurns = true }, content);

        _extracted.Should().ContainSingle();
    }

    [Fact]
    public async Task Off_by_default_every_turn_is_extracted()
    {
        await SayAsync(new AgentFrameworkOptions(), "What's a good hike?");

        _extracted.Should().ContainSingle();
    }

    [Fact]
    public async Task One_owner_s_question_never_joins_another_owner_s_extraction()
    {
        var options = new AgentFrameworkOptions { DeferQuestionTurns = true };

        await SayAsync(options, "What's a good hike?", owner: "u1");
        await SayAsync(options, "I moved to Porto.", owner: "u2");

        _extracted.Should().ContainSingle().Which.Messages.Select(m => m.Content).Should().Equal("I moved to Porto.");
    }

    [Fact]
    public async Task Hosts_do_not_share_what_waits()
    {
        await SayAsync(new AgentFrameworkOptions { DeferQuestionTurns = true }, "What's a good hike?");
        await SayAsync(new AgentFrameworkOptions { DeferQuestionTurns = true }, "I moved to Porto.");

        _extracted.Should().ContainSingle().Which.Messages.Should().ContainSingle();
    }
}
