using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// H-2: a plain question does not pay for an extraction call; a question that might carry a fact still does.
/// </summary>
/// <remarks>
/// Measured: with IgnoreQuestions the model returned nothing for “What do you know about my family?” and
/// “Where does my brother live?”, yet each call cost 1.0–1.1 s and ≈450 prompt tokens.
/// </remarks>
public sealed class PlainQuestionGateTests
{
    [Theory]
    [InlineData("Where does my brother live?")]
    [InlineData("What do you know about my family?")]
    [InlineData("What is my dog's name?")]
    [InlineData("Do I have a sister?")]
    [InlineData("Is it raining? Should I take an umbrella?")]
    public void A_plain_question_is_recognised(string text) =>
        ExtractionNoveltyGate.IsPlainQuestion(text).Should().BeTrue();

    [Theory]
    [InlineData("What does Dana do for work?")]                        // a name: it may be new
    [InlineData("Can you help me plan my trip to Seville next week?")]  // a plan, with a time
    [InlineData("Did you know I moved to madrid?")]                     // the user stating something
    [InlineData("Is my appointment on the 12th?")]                      // a number
    [InlineData("My brother lives in Seville. Where does he work?")]    // a statement, then a question
    [InlineData("What are we doing tomorrow?")]                         // a time word
    [InlineData("Lena is visiting me in October.")]                     // not a question at all
    // The review's cases: questions that state something about the user.
    [InlineData("Can you suggest a gift for my sister who loves gardening?")]
    [InlineData("did i tell you i got engaged?")]
    [InlineData("do you remember my dog is allergic to chicken?")]
    [InlineData("What should I cook for my vegetarian husband?")]
    [InlineData("Do you know that my brother moved?")]
    // Review round 2: a possessive followed by a description of what is owned.
    [InlineData("Can you recommend a gluten-free bakery for my celiac son?")]
    [InlineData("Can I bring my peanut-allergic son to the party?")]
    public void A_question_that_might_carry_a_fact_is_not(string text) =>
        ExtractionNoveltyGate.IsPlainQuestion(text).Should().BeFalse();

    private readonly IFactExtractor _extractor = Substitute.For<IFactExtractor>();

    private ExtractionStage Stage(bool skipPlainQuestions)
    {
        _extractor.ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExtractedFact>>([]));
        return new([], [_extractor], [], [], [], Substitute.For<IEntityResolver>(),
            Options.Create(new ExtractionOptions { SkipPlainQuestions = skipPlainQuestions }),
            NullLogger<ExtractionStage>.Instance);
    }

    private static Message M(string content) => new()
    {
        MessageId = "m-1", ConversationId = "c-1", SessionId = "s-1", Role = "user", Content = content,
        TimestampUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task With_the_option_a_plain_question_never_reaches_the_extractor()
    {
        await Stage(skipPlainQuestions: true).ExtractAsync([M("Where does my brother live?")], ExtractionTypes.All);

        await _extractor.DidNotReceive().ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_the_option_a_question_naming_someone_is_still_extracted()
    {
        await Stage(skipPlainQuestions: true).ExtractAsync([M("What does Dana do for work?")], ExtractionTypes.All);

        await _extractor.Received(1).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_the_option_nothing_changes()
    {
        await Stage(skipPlainQuestions: false).ExtractAsync([M("Where does my brother live?")], ExtractionTypes.All);

        await _extractor.Received(1).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_plain_question_does_not_hide_a_greeting_rule_that_is_off()
    {
        // Only the question rule on: a “thanks” turn is still extracted (E4 is its own switch).
        await Stage(skipPlainQuestions: true).ExtractAsync([M("thanks!")], ExtractionTypes.All);

        await _extractor.Received(1).ExtractAsync(Arg.Any<IReadOnlyList<Message>>(), Arg.Any<CancellationToken>());
    }
}
