using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// A forgotten message is never quoted back as a fact's source (G-30 review): the fact may still be live, but the
/// words that were forgotten must not return with it.
/// </summary>
public sealed class ForgottenSourceQuoteTests
{
    private static Message M(string id, DateTimeOffset? forgotten) => new()
    {
        MessageId = id, ConversationId = "c", SessionId = "s", Role = "user", Content = $"said {id}",
        TimestampUtc = DateTimeOffset.UnixEpoch, InvalidatedAtUtc = forgotten,
    };

    [Fact]
    public async Task A_forgotten_message_is_not_a_source()
    {
        var messages = Substitute.For<IMessageRepository>();
        messages.GetByIdsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([M("m-live", null), M("m-forgotten", DateTimeOffset.UtcNow)]);
        var fact = new Fact
        {
            FactId = "f", Subject = "user", Predicate = "has brother", Object = "someone", Confidence = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceMessageIds = ["m-live", "m-forgotten"],
        };
        var state = new ProjectionState
        {
            Options = new MemoryProjectionOptions(), Scope = null,
            Entities = [], Facts = [fact], Preferences = [], Traces = [], RecentMessages = [], RelevantMessages = [],
            EntityScores = [], FactScores = [], PreferenceScores = [], TraceScores = [],
        };

        var sources = await state.GetSourceMessagesAsync(messages, CancellationToken.None);

        sources.Keys.Should().Equal("m-live");
    }
}
