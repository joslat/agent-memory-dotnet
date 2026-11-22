using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>RECALL-NAMESAKE-LEAK: which recalled items are about a namesake of the one the question means.</summary>
public sealed class NamesakeFilterTests
{
    private static RecalledItemEntity Link(string item, string entityId, string name, string? source = null, string[]? knownAs = null,
        string[]? aliases = null) => new()
    {
        ItemId = item,
        Entity = new Entity { EntityId = entityId, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch, Aliases = aliases ?? [] },
        KnownAs = knownAs ?? [],
        SourceTexts = source is null ? [] : [source],
    };

    private static readonly RecalledItemEntity[] Animals =
    [
        Link("dog-age", "dog", "Luna", "My dog Luna is a border collie."),
        Link("dog-knee", "dog", "Luna", "My dog Luna is a border collie."),
        Link("cat-breed", "cat", "Luna", "My sister's cat is also called Luna, a grey Persian."),
        Link("cat-sofa", "cat", "Luna", "My sister's cat is also called Luna, a grey Persian."),
    ];

    [Fact]
    public void A_relation_the_question_names_picks_the_one_known_by_it_and_drops_the_namesake_known_otherwise()
    {
        var (drop, entities) = NamesakeFilter.Decide("How old is my dog?", Animals);
        drop.Should().BeEquivalentTo(["cat-breed", "cat-sofa"]);
        entities.Should().Equal("cat");
    }

    [Fact]
    public void A_question_that_picks_out_none_drops_nothing()
    {
        NamesakeFilter.Decide("How old is Luna?", Animals).DropItems.Should().BeEmpty("both are called Luna and no relation is named");
        NamesakeFilter.Decide("What did I eat today?", Animals).DropItems.Should().BeEmpty();
    }

    [Fact]
    public void A_namesake_with_no_known_role_is_kept()
    {
        // It may be the dog, split in two: a split costs recall, a wrong drop costs the answer.
        var links = new[] { Animals[0], Link("unknown-luna", "luna-2", "Luna", "Luna barked all night.") };
        NamesakeFilter.Decide("How old is my dog?", links).DropItems.Should().BeEmpty();
    }

    [Fact]
    public void An_item_also_about_the_one_meant_is_kept()
    {
        var links = Animals.Append(Link("cat-breed", "dog", "Luna", "My dog Luna is a border collie.")).ToArray();
        NamesakeFilter.Decide("How old is my dog?", links).DropItems.Should().BeEquivalentTo(["cat-sofa"]);
    }

    [Fact]
    public void A_spelling_only_one_namesake_has_picks_it()
    {
        var links = new[]
        {
            Link("madrid", "designer", "Valentina Ríos", "Valentina Ríos is the designer on Atlas.", aliases: ["Vale"]),
            Link("tooth", "niece", "Valentina", "My niece Valentina started school this year."),
        };
        var (drop, _) = NamesakeFilter.Decide("What has Vale been working on, and where is she now?", links);
        drop.Should().BeEquivalentTo(["tooth"]);
        NamesakeFilter.Decide("What is new with my niece?", links).DropItems.Should().BeEquivalentTo(["madrid"]);
    }

    [Fact]
    public void What_an_entity_is_known_as_comes_from_its_facts_too()
    {
        var links = new[]
        {
            Link("madrid", "designer", "Valentina Ríos", knownAs: ["is the designer on Atlas"]),
            Link("tooth", "niece", "Valentina", knownAs: ["has niece"]),
        };
        NamesakeFilter.Decide("What is new with my niece?", links).DropItems.Should().BeEquivalentTo(["madrid"]);
    }

    [Fact]
    public void Entities_that_share_no_name_word_are_no_namesakes()
    {
        var links = new[] { Link("a", "dog", "Luna", "My dog Luna is a border collie."), Link("b", "cat", "Michi", "My sister's cat Michi.") };
        NamesakeFilter.Decide("How old is my dog?", links).DropItems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("My sister's cat is also called Luna, a grey Persian.", "cat")]
    [InlineData("My sister's cat Luna sleeps on the sofa.", "cat")]
    [InlineData("A puppy named Luna moved in next door.", "dog")]
    [InlineData("Luna is the designer on Atlas.", "designer")]
    [InlineData("Luna slept all day.", null)]
    public void What_a_text_says_a_name_is(string text, string? expected)
    {
        var found = IdentityRelationCue.InText(text, ["Luna"]);
        if (expected is null) found.Should().BeEmpty();
        else found.Should().Contain(expected);
    }

    [Theory]
    [InlineData("How old is my dog, and what happened to her knee?", "dog")]
    [InlineData("What is new with my little niece?", "niece")]
    [InlineData("What has Vale been working on?", null)]
    public void What_relation_a_question_names(string question, string? expected)
    {
        var found = IdentityRelationCue.InQuestion(question);
        if (expected is null) found.Should().BeEmpty();
        else found.Should().Contain(expected);
    }
}
