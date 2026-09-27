using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// A forgotten message is never quoted back as a fact's source (G-30 review): the fact may still be live, but the
/// words that were forgotten must not return with it. Date grounding still reads its timestamp (a date only).
/// </summary>
public sealed class ForgottenSourceQuoteTests
{
    private static Message M(string id, string text, DateTimeOffset? forgotten) => new()
    {
        MessageId = id, ConversationId = "c", SessionId = "s", Role = "user", Content = text,
        TimestampUtc = DateTimeOffset.UnixEpoch, InvalidatedAtUtc = forgotten,
    };

    private static Fact F(params string[] sources) => new()
    {
        FactId = "f", Subject = "user", Predicate = "lives in", Object = "Seville", Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceMessageIds = sources,
    };

    [Fact]
    public void A_forgotten_message_is_never_the_quote()
    {
        var sources = new Dictionary<string, Message>
        {
            ["m-forgotten"] = M("m-forgotten", "My brother Pablo lives in Seville.", DateTimeOffset.UtcNow),
        };

        SourceQuoteProjectionFeature.SelectQuote(F("m-forgotten"), sources, 200).Should().BeNull();
    }

    [Fact]
    public void A_live_source_is_still_quoted_beside_a_forgotten_one()
    {
        var sources = new Dictionary<string, Message>
        {
            ["m-forgotten"] = M("m-forgotten", "Pablo lives in Seville.", DateTimeOffset.UtcNow),
            ["m-live"] = M("m-live", "I moved to Seville last year.", null),
        };

        SourceQuoteProjectionFeature.SelectQuote(F("m-forgotten", "m-live"), sources, 200)
            .Should().Contain("moved to Seville").And.NotContain("Pablo");
    }
}
