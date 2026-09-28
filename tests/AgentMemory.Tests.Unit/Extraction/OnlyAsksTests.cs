using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// 36.5: the generous rule that defers a turn's extraction, beside the strict one that skips it (H-2). Deferring loses
/// nothing, so it may hold a question that carries a fact; skipping may not. Every plain question only asks.
/// </summary>
public sealed class OnlyAsksTests
{
    private static IReadOnlyList<Message> Turn(string content, string role = "user") =>
    [
        new Message
        {
            MessageId = "m", SessionId = "s", ConversationId = "c", Role = role, Content = content, TimestampUtc = DateTimeOffset.UnixEpoch,
        },
    ];

    [Theory]
    [InlineData("Where does my brother live?")]
    [InlineData("What do you know about my family?")]
    [InlineData("Do I have a sister?")]
    [InlineData("Is it raining? Should I take an umbrella?")]
    public void Every_plain_question_only_asks(string text)
    {
        ExtractionNoveltyGate.IsPlainQuestion(text).Should().BeTrue("the premise: the strict rule would skip it");
        ExtractionNoveltyGate.OnlyAsks(Turn(text)).Should().BeTrue();
    }

    [Theory]
    [InlineData("What should I get my sister Ana, who loves pottery?")]
    [InlineData("Can you suggest a gift for my sister who loves gardening?")]
    [InlineData("Recommend a hike near Sintra.")]
    public void A_question_that_may_carry_a_fact_still_only_asks_and_waits(string text)
    {
        ExtractionNoveltyGate.IsPlainQuestion(text).Should().BeFalse("the strict rule must not skip it");
        ExtractionNoveltyGate.OnlyAsks(Turn(text)).Should().BeTrue("deferring loses nothing");
    }

    [Theory]
    [InlineData("My brother lives in Seville. Where does he work?")]
    [InlineData("do you remember my dog is allergic to chicken?")]
    [InlineData("Please note that I'm vegetarian.")]
    [InlineData("Hi!")]
    [InlineData("")]
    public void A_turn_that_tells_or_says_nothing_to_hold_does_not_only_ask(string text)
    {
        ExtractionNoveltyGate.OnlyAsks(Turn(text)).Should().BeFalse();
    }

    [Fact]
    public void Only_what_the_user_said_counts()
    {
        ExtractionNoveltyGate.OnlyAsks(Turn("Would you like a summary?", role: "assistant")).Should().BeFalse();
    }
}
