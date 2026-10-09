using FluentAssertions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>The preference source check on the host's chat model (AMDREAM001): Dreaming round 1's P3q question.</summary>
public sealed class ChatModelPreferenceSourceCheckTests
{
    private static readonly PreferenceSourceRequest Request = new(
        "Helena's octopus à lagareiro got me eating octopus!",
        new DateTimeOffset(2027, 9, 15, 10, 0, 0, TimeSpan.Zero),
        ["We had dinner at Helena's on Sunday."],
        [new PreferenceSourceItem("p1", "food", "loves octopus"), new PreferenceSourceItem("p2", "values", "values family traditions")]);

    [Fact]
    public void The_question_shows_the_message_its_date_the_earlier_message_and_each_preference_with_its_category()
    {
        var messages = ChatModelPreferenceSourceCheck.Messages(Request);

        messages[0].Text.Should().Contain("sent on Wednesday 15 September 2027")
            .And.Contain("\"partly\": part of it is said and part is added")
            .And.Contain("{\"M1\": \"said\", \"M2\": \"partly\", \"M3\": \"guess\", ...}");
        messages[1].Text.Should().Be(
            "EARLIER: the person: We had dinner at Helena's on Sunday.\n"
            + "MESSAGE: the person: Helena's octopus à lagareiro got me eating octopus!\n\n"
            + "STORED:\nM1: preference: loves octopus (food)\nM2: preference: values family traditions (values)");
    }

    [Fact]
    public void Only_a_guess_is_returned_and_an_answer_without_json_returns_nothing()
    {
        ChatModelPreferenceSourceCheck.Parse("Sure: {\"M1\": \"partly\", \"M2\": \"Guess\"}", Request.Preferences).Should().Equal("p2");
        ChatModelPreferenceSourceCheck.Parse("{\"M1\": \"said\"}", Request.Preferences).Should().BeEmpty(); // M2 left out: kept
        ChatModelPreferenceSourceCheck.Parse("The second one is a guess.", Request.Preferences).Should().BeEmpty();
    }
}
