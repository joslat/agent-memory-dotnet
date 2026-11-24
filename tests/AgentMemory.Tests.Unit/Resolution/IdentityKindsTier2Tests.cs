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
        Key.Mentions.Should().HaveCount(70, "key version 2 labels Marta in her wedding's two statements too");
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
        score.ByLevel.Values.Sum(l => l.Right).Should().Be(70);
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

    /// <summary>
    /// The second paid check of 23 Nov (ikb-cover2-1, 2): the writer stored Clara's cat as "Clara's cat Luna" (PERSON, as it
    /// types pets), and the scorer counted it as a split of Clara (T25) through the possessive's owner. A stored mention's
    /// possessive owner is not what it names, as a key name's owner is not (A_mention_of_a_possessives_owner...).
    /// </summary>
    [Fact]
    public void A_stored_mention_named_with_its_owners_possessive_is_the_owned_thing()
    {
        var withoutTheCat = PerfectEntities().Where(e => !e.ToString()!.Contains("e-T06", StringComparison.Ordinal)).ToArray();
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. withoutTheCat, Entity("e-cat", "Clara's cat Luna", "PERSON", StatementsOf("T06"))]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Where(m => m.Identity == "e-cat").Should().OnlyContain(m => m.Thing == "T06");
        score.SplitThings.Should().NotContainKey("T25");
    }

    /// <summary>
    /// The third paid check (cover3-1, cover3-2): the writer stored the cat as "Luna, Clara's cat". The possessive strip took
    /// everything up to the owner, "Luna" too, so the mention lost its own name. The owner is one word.
    /// </summary>
    [Fact]
    public void A_stored_mention_that_names_the_owned_thing_before_its_owner_keeps_its_own_name()
    {
        var withoutTheCat = PerfectEntities().Where(e => !e.ToString()!.Contains("e-T06", StringComparison.Ordinal)).ToArray();
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. withoutTheCat, Entity("e-cat", "Luna, Clara's cat", "PERSON", StatementsOf("T06"))]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Where(m => m.Identity == "e-cat").Should().OnlyContain(m => m.Thing == "T06");
        score.SplitThings.Should().NotContainKey("T25");
        IdentityKindsScorer.MentionWords("Luna, Clara's cat").Should().Contain("luna").And.NotContain("clara");
    }

    /// <summary>
    /// IKB-SCORER-PLACE-MENTION, the fourth paid check (cover4-1, statement 23): the writer stored "the mosque in Córdoba", a
    /// thing the key does not hold, and the scorer counted it as Córdoba in Spain. A common-noun head with the place it is in
    /// names the head; a proper name with its place ("Córdoba in Spain") still tells namesakes apart.
    /// </summary>
    [Fact]
    public void A_stored_thing_named_with_the_place_it_is_in_is_not_that_place()
    {
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. PerfectEntities(), Entity("e-mosque", "the mosque in Córdoba", "OBJECT", [23])]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Where(m => m.Identity == "e-mosque").Should().OnlyContain(m => m.Thing == null);
        score.SplitThings.Should().NotContainKey("T10");
        IdentityKindsScorer.MentionWords("the mosque in Córdoba").Should().Equal("mosque");
        IdentityKindsScorer.MentionWords("Córdoba in Spain").Should().Contain("spain");
    }

    /// <summary>
    /// IHC-SCORER (the owner's call, 23 Nov): whos-who-v2's baseline counted two wrong merges that were the scorer's. The
    /// writer stored "Rebecca Thornton's wedding" as an event where the friend's engagement was told (statement 16, where
    /// the key names only the friend) and found it again where the wedding is named; the scorer, finding no event in
    /// statement 16, labelled the event as the friend by its name words. An event named after a thing is not that thing;
    /// the thing's bare name stored under the wrong type still is (a model may type a project an event).
    /// </summary>
    [Fact]
    public void An_event_named_after_a_thing_is_not_that_thing()
    {
        // A two-word name keeps its first word when the possessive is stripped, as "Rebecca Thornton's" kept "Rebecca".
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. PerfectEntities(),
                Entity("e-lunch", "Valentina Ríos's leaving lunch", "EVENT", [11]),
                Entity("e-typed-event", "Vale", "EVENT", [42])]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Single(m => m.Identity == "e-lunch").Thing.Should().BeNull();
        score.Mentions.Single(m => m.Identity == "e-typed-event").Thing.Should().Be("T01");
        score.SplitThings.Keys.Should().Equal("T01");
        score.WrongMerges.Should().Be(0);
    }

    /// <summary>
    /// IHC-SCORER, the root of the case above: a possessive's owner was one word, so "Dr. Sosa's clinic" kept "dr" and was
    /// counted as the vet (the paid check of 24 Nov, ihc1-v1-glm-1, statement 49), as "Rebecca Thornton's" kept "rebecca".
    /// An owner of several capitalised words (a title too) is the owner; "Luna, Clara's cat" keeps "Luna".
    /// </summary>
    [Fact]
    public void A_thing_named_after_an_owner_of_several_words_is_not_that_owner()
    {
        var score = IdentityKindsScorer.Score(Key, WhosWho, Store([.. PerfectEntities(),
                Entity("e-clinic", "Dr. Sosa's clinic", "LOCATION", [49])]),
            IdentityGoldScorer.EntitiesView);

        score.Mentions.Single(m => m.Identity == "e-clinic").Thing.Should().BeNull();
        score.SplitThings.Should().NotContainKey("T04");
        IdentityKindsScorer.MentionWords("Dr. Sosa's clinic").Should().Equal("clinic");
        IdentityKindsScorer.MentionWords("Valentina Ríos's leaving lunch").Should().BeEquivalentTo(["leaving", "lunch"]);
        IdentityKindsScorer.MentionWords("Luna, Clara's cat").Should().Contain("luna");
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

    /// <summary>
    /// IKB-SCORER-MUSTNOT-SCOPE (found by IKB-TIER2-RERUN): gold v1's niece question forbids "Madrid" anywhere, so the
    /// company's own fact ("Brisa Labs opened an office in Madrid") counted as the designer's namesake fact. A forbidden
    /// fact is already every group of its mentions in one prompt item, so a gold v2 scopes it to the namesake by adding
    /// her names as a second group; no scorer change is needed, and v1 stays frozen as it is.
    /// </summary>
    [Fact]
    public void A_forbidden_fact_scoped_to_the_namesake_ignores_the_same_word_in_another_thing_s_fact()
    {
        var niece = WhosWho.Questions.Single(q => q.Id == "niece");
        var scoped = niece with { MustNot = [new([["Madrid"], ["Valentina", "Vale"]])] };
        const string company = "Known facts: Valentina started school; Brisa Labs opened an office in Madrid";

        Checks.Forbidden(niece, company).Should().Contain(f => f.Found, "v1 counts the word anywhere, as frozen");
        Checks.Forbidden(scoped, company).Should().OnlyContain(f => !f.Found);
        Checks.Forbidden(scoped, "Known facts: Valentina moved to the Madrid office").Should().Contain(f => f.Found);
    }
}
