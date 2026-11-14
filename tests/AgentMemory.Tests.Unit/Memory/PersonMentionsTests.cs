using AgentMemory.Core.Memory;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// ILC-33: which confirmed people a question names. Whole words, case and accents aside; a name two people share names
/// neither, so recall never guesses between homonyms.
/// </summary>
public sealed class PersonMentionsTests
{
    private static PersonMentions.Person P(string id, params string[] surfaces) => new(id, surfaces);

    [Fact]
    public void A_unique_name_in_the_question_names_that_person() =>
        PersonMentions.Named("I have my mentoring session with Valentina tomorrow.",
            [P("v", "Valentina"), P("t", "Tomás")]).Should().Equal("v");

    [Fact]
    public void A_name_two_people_share_names_neither() =>
        PersonMentions.Named("What do I know about Alex Kim?", [P("cousin", "Alex Kim"), P("colleague", "Alex Kim", "AK")])
            .Should().BeEmpty();

    [Fact]
    public void An_open_proposal_is_never_named_and_makes_its_namesake_ambiguous()
    {
        PersonMentions.Named("What do I know about Alex Kim?", [P("cousin", "Alex Kim"), new("pending", ["Alex Kim"], Confirmed: false)])
            .Should().BeEmpty("it is not yet known which Alex Kim this is");
        PersonMentions.Named("Ping AK.", [new("pending", ["AK"], Confirmed: false)]).Should().BeEmpty();
    }

    [Fact]
    public void An_alias_only_one_person_has_still_names_them() =>
        PersonMentions.Named("Ping AK about the invoices.", [P("cousin", "Alex Kim"), P("colleague", "Alex Kim", "AK")])
            .Should().Equal("colleague");

    [Fact]
    public void A_name_inside_a_longer_name_the_question_says_is_part_of_it()
    {
        // REV1-M3: "Ana" is someone else; "Ana García" names only Ana García.
        PersonMentions.Named("Where does Ana García work?", [P("ana", "Ana"), P("ana-garcia", "Ana García")]).Should().Equal("ana-garcia");
        PersonMentions.Named("Ana met Ana García.", [P("ana", "Ana"), P("ana-garcia", "Ana García")]).Should().Equal("ana", "ana-garcia");
        PersonMentions.Named("Where does Ana García work?", [P("ana", "Ana"), P("ag1", "Ana García"), P("ag2", "Ana García")])
            .Should().BeEmpty("the longer name is ambiguous, and the shorter one is only part of it");
    }

    [Fact]
    public void A_name_that_is_an_everyday_word_counts_only_written_as_a_name()
    {
        // REV1-L11.
        PersonMentions.Named("Will it rain tomorrow?", [P("will", "Will")]).Should().BeEmpty();
        PersonMentions.Named("may I ask about the invoices?", [P("may", "May")]).Should().BeEmpty();
        PersonMentions.Named("I met Will yesterday. What did he say?", [P("will", "Will")]).Should().Equal("will");
        PersonMentions.Named("valentina called", [P("v", "Valentina")]).Should().Equal(["v"], "an ordinary name still matches in any case");
        PersonMentions.Named("Will Smith called.", [P("ws", "Will Smith")]).Should().Equal(["ws"], "a longer name is not an everyday word");
    }

    [Fact]
    public void Part_of_a_word_is_not_a_name() =>
        PersonMentions.Named("Valentinas and Tomásito are not here.", [P("v", "Valentina"), P("t", "Tomás")]).Should().BeEmpty();

    [Fact]
    public void Case_and_accents_do_not_matter() =>
        PersonMentions.Named("did martin sign the rollback plan?", [P("m", "Martín")]).Should().Equal("m");

    [Fact]
    public void People_come_in_the_order_the_question_names_them_and_at_most_three() =>
        PersonMentions.Named("Diego, Tomás, Valentina, Martín and Carolina",
            [P("c", "Carolina"), P("m", "Martín"), P("v", "Valentina"), P("t", "Tomás"), P("d", "Diego")])
            .Should().Equal("d", "t", "v");

    [Fact]
    public void A_one_letter_surface_names_nobody() =>
        PersonMentions.Named("I owe J a call.", [P("j", "J")]).Should().BeEmpty();
}
