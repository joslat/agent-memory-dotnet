using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Scenarios;
using AgentMemory.Scenarios.PublicGold;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// IKB-2: the Identity Kinds Benchmark's tier 2 answer key and scorer, on the frozen whos-who-v1 conversation and stores
/// written here. No model, no store.
/// </summary>
public sealed class IdentityKindsTier2Tests
{
    private static string Folder([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../../../tools/AgentMemory.Scenarios/scenarios/identity-kinds"));

    private static readonly Scenario WhosWho = Scenario.Load(Path.Combine(Folder(), "whos-who-v1.json"));
    private static readonly IdentityKindsGold Key = IdentityKindsGold.Load(Path.Combine(Folder(), "whos-who-v1.gold.json"));

    private static IdentityKindsGold.Thing Thing(string id) => Key.Things.Single(t => t.Id == id);

    private static int[] StatementsOf(string thing) => Key.Mentions.Where(m => m.Thing == thing).Select(m => m.Statement).Distinct().Order().ToArray();

    private static object Entity(string id, string name, string type, IEnumerable<int> statements, params string[] aliases)
    {
        var texts = WhosWho.Turns().Select(t => t.Text).ToList();
        return new { id, name, type, aliases, sources = statements.Select(n => texts[n - 1]).ToArray() };
    }

    private static JsonElement Store(params object[] entities) =>
        JsonSerializer.SerializeToElement(new { identity = (object?)null, entities = new { entities, sameAs = Array.Empty<object>() } });

    /// <summary>One entity per real thing, named as its description names it, from every statement that mentions it.</summary>
    private static object[] PerfectEntities() =>
        Key.Things.Select(t => Entity("e-" + t.Id, IdentityKindsGold.NamesOf(t)[0], t.Type, StatementsOf(t.Id))).ToArray();

    [Fact]
    public void The_frozen_key_fits_the_frozen_conversation()
    {
        Key.CheckAgainst(WhosWho);

        WhosWho.Turns().Should().HaveCount(54);
        Key.Things.Should().HaveCount(27);
        Key.Namesakes.Should().HaveCount(9);
        Key.Mentions.Should().HaveCount(68);
        Key.Things.Select(t => t.Kind).Distinct().Should().HaveCount(14);
        WhosWho.Questions.Should().HaveCount(7);
        // Five questions forbid a namesake's facts; "cordoba" and "brisa" ask about one thing only.
        WhosWho.Questions.Where(q => q.MustNot is { Count: > 0 }).Select(q => q.Id)
            .Should().Equal("dog", "niece", "vale", "tortoni", "atlas");
    }

    [Fact]
    public void A_key_that_does_not_fit_the_conversation_is_refused()
    {
        var moved = Key with { Mentions = [Key.Mentions[0] with { Message = Key.Mentions[0].Message + 1 }, .. Key.Mentions.Skip(1)] };

        var check = () => moved.CheckAgainst(WhosWho);

        check.Should().Throw<InvalidDataException>().WithMessage("*statement 1 is not message 2 of session 1*");
    }

    [Fact]
    public void A_thing_goes_by_its_description_and_its_quoted_names_but_not_a_possessive()
    {
        IdentityKindsGold.NamesOf(Thing("T01")).Should().Equal("Valentina Ríos", "Vale");
        IdentityKindsGold.NamesOf(Thing("T05")).Should().Equal("Luna", "Lunita");
        IdentityKindsGold.NamesOf(Thing("T17")).Should().Equal("Inés's Corolla", "the car");
        IdentityKindsScorer.Words("Inés's Corolla").Should().BeEquivalentTo(["ines", "corolla"]);
    }

    [Fact]
    public void A_store_with_one_identity_per_real_thing_has_no_wrong_merge_and_no_split()
    {
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store(PerfectEntities()), IdentityGoldScorer.EntitiesView);

        score.WrongMerges.Should().Be(0);
        score.Splits.Should().Be(0);
        score.Missed.Should().Be(0, string.Join("; ", score.Mentions.Where(m => m.Thing is null).Select(m => $"{m.Words} in {m.Statement}: {m.How}")));
        score.Right.Should().Be(27);
        score.ByLevel.Values.Sum(l => l.Right).Should().Be(68);
    }

    [Fact]
    public void One_identity_for_the_dog_and_the_cat_is_a_wrong_merge_of_two_animals()
    {
        var entities = PerfectEntities().Where(e => ((dynamic)e).id is not ("e-T05" or "e-T06")).Append(
            Entity("e-luna", "Luna", "OBJECT", StatementsOf("T05").Concat(StatementsOf("T06")).Distinct().Order())).ToArray();

        var score = IdentityKindsScorer.Score(Key, WhosWho, Store(entities), IdentityGoldScorer.EntitiesView);

        score.WrongMerges.Should().Be(1);
        score.WrongMergeDetail["e-luna"].Should().Equal("T05", "T06");
        score.ByKind["animal"].Merged.Should().Be(2);
        score.ByKind["animal"].Right.Should().Be(0);
        score.Right.Should().Be(25);
    }

    [Fact]
    public void Two_identities_for_one_person_are_a_split_counted_under_its_kind()
    {
        var statements = StatementsOf("T01");
        var entities = PerfectEntities().Where(e => ((dynamic)e).id != "e-T01").Concat(
        [
            Entity("e-vale-1", "Valentina Ríos", "PERSON", statements.Take(1)),
            Entity("e-vale-2", "Vale", "PERSON", statements.Skip(1)),
        ]).ToArray();

        var score = IdentityKindsScorer.Score(Key, WhosWho, Store(entities), IdentityGoldScorer.EntitiesView);

        score.WrongMerges.Should().Be(0);
        score.Splits.Should().Be(1);
        score.SplitThings.Should().ContainKey("T01").WhoseValue.Should().Be(2);
        score.ByKind["person"].Splits.Should().Be(1);
        score.Right.Should().Be(26);
    }

    [Fact]
    public void The_owner_and_a_thing_the_key_does_not_hold_stay_unlabelled()
    {
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. PerfectEntities(), Entity("e-ines", "Inés", "PERSON", [1])]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Single(m => m.Identity == "e-ines").Thing.Should().BeNull();
        score.WrongMerges.Should().Be(0);
        score.Right.Should().Be(27);
    }

    /// <summary>
    /// The tier 2 runs of 22 Nov counted the product T16 ("her mother's Kindle") as right when the writer stored only
    /// "user's mother" as a person: the possessive's owner made "mother" a name word of the Kindle.
    /// </summary>
    [Fact]
    public void A_mention_of_a_possessives_owner_is_not_the_thing_it_owns()
    {
        var withoutTheKindle = PerfectEntities().Where(e => !e.ToString()!.Contains("e-T16", StringComparison.Ordinal)).ToArray();
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. withoutTheKindle, Entity("e-mother", "user's mother", "PERSON", StatementsOf("T16"))]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Single(m => m.Identity == "e-mother").Thing.Should().NotBe("T16");
        score.ByKind["product"].Missed.Should().Be(1);
    }

    /// <summary>
    /// The paid check of 23 Nov (ikb-cover-1, 2): the writer stored the city "Buenos Aires" (statement 19) and the town
    /// "Villa Carlos Paz" (statement 6) as places of their own, which is right, and the scorer counted them as splits of
    /// T07 ("Café Tortoni in Buenos Aires") and T11 ("the family lake house in Villa Carlos Paz"): by the words of the
    /// "in ..." qualifier, and by type alone when nothing else matched. A place a thing is "in" is not that thing.
    /// </summary>
    [Fact]
    public void The_place_a_thing_is_in_is_not_that_thing()
    {
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. PerfectEntities(),
                Entity("e-city", "Buenos Aires", "LOCATION", [19]), Entity("e-town", "Villa Carlos Paz", "LOCATION", [6])]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Single(m => m.Identity == "e-city").Thing.Should().BeNull();
        score.Mentions.Single(m => m.Identity == "e-town").Thing.Should().BeNull();
        score.Splits.Should().Be(0);
        score.Right.Should().Be(27);
    }

    [Fact]
    public void A_role_noun_still_finds_the_one_thing_of_its_type()
    {
        var withoutTheVet = PerfectEntities().Where(e => !e.ToString()!.Contains("e-T04", StringComparison.Ordinal)).ToArray();
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. withoutTheVet, Entity("e-vet", "the vet", "PERSON", StatementsOf("T04"))]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Where(m => m.Identity == "e-vet").Should().OnlyContain(m => m.Thing == "T04");
    }

    [Fact]
    public void A_namesakes_fact_in_the_prompt_is_found_and_its_absence_passes()
    {
        var dog = WhosWho.Questions.Single(q => q.Id == "dog");

        Checks.Forbidden(dog, "Known facts: Luna is four; Clara's cat is a grey Persian").Should().Contain(f => f.Found);
        Checks.Forbidden(dog, "Known facts: Luna is four; Luna had knee surgery").Should().OnlyContain(f => !f.Found);
    }
}
