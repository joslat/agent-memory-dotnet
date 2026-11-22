namespace AgentMemory.Tests.IdentityKinds;

/// <summary>
/// The Identity Kinds Benchmark, tier 1 (IKB-1): every kind of thing crossed with every situation, as short conversations.
/// Each case is generated from one kind (a person, a pet, a cafe, a product ...) and one situation (named again, named
/// again much later, a namesake, a changed type, turns in another order ...). The extraction of every turn is given, so
/// no model is called: the result is what the product's own identity rules do once a type has been assigned, the same on
/// every run. Which type a real model assigns is tier 2. Ported unchanged from the harness that recorded the 2.3.1
/// baseline (strategy/features/evidence-based-identity/validation/identity-kinds). The unit tests check the generator;
/// the Neo4j tests link this file and run the cases.
/// </summary>
public static class IdentityKindCases
{
    /// <summary>Mentions of things numbered from here are unrelated filler, not one of the case's real things.</summary>
    public const int FillerThing = 100;

    /// <summary>One kind of thing: a name that two different things share, its type, a type a model might give instead.</summary>
    public sealed record Kind(string Id, string Name, string Type, string AltType, string[] A, string B);

    /// <summary>One mention in a turn: the name, the type the extraction gives it, and which real thing it is about.</summary>
    public sealed record Mention(string Name, string Type, int Thing);

    /// <summary>One turn: its text, its mentions, and where it sits in time (session and seconds from the start).</summary>
    public sealed record Turn(string Text, Mention[] Mentions, string Session = "s1", double AtSeconds = 0);

    public sealed record Case(string Id, string Kind, string Situation, string What, int RealThings, Turn[] Turns, int? LastMustJoinThing = null);

    public static readonly Kind[] Kinds =
    [
        new("person", "Valentina", "PERSON", "PERSON",
            ["My colleague Valentina joined the platform team.", "Valentina is leading the migration.", "Valentina moved to Madrid."],
            "My niece Valentina started school this year."),
        // The name is written the same way in every sentence: the writer's quote must be found in the turn, letter for letter.
        new("event", "Marta's wedding", "EVENT", "EVENT",
            ["Marta's wedding is on 14 March.", "Marta's wedding will be in Bariloche.", "I am the best man at Marta's wedding."],
            "My cousin is another Marta, and Marta's wedding is in June in Mendoza."),
        new("company", "Acme", "ORGANIZATION", "LOCATION",
            ["I started at Acme in March.", "Acme is moving its office.", "Acme pays on the 25th."],
            "My brother's gym is also called Acme."),
        new("team", "platform team", "ORGANIZATION", "OBJECT",
            ["The platform team ships on Thursdays.", "The platform team owns the billing service.", "The platform team is hiring."],
            "At my old job the platform team ran the data centre."),
        new("brand", "Patagonia", "ORGANIZATION", "OBJECT",
            ["I only buy jackets from Patagonia.", "Patagonia repairs its clothes for free.", "Patagonia opened a store downtown."],
            "Patagonia is also the name of my cousin's bakery."),
        new("pet", "Luna", "OBJECT", "PERSON",
            ["My dog Luna is a border collie.", "Luna is four years old.", "Luna had knee surgery in May."],
            "My sister's cat is also called Luna, a grey Persian."),
        new("venue", "Café Tortoni", "LOCATION", "ORGANIZATION",
            ["We met at Café Tortoni.", "Café Tortoni closes at midnight.", "Café Tortoni has live tango on Fridays."],
            "There is another Café Tortoni in Rosario, much smaller."),
        new("town", "Córdoba", "LOCATION", "LOCATION",
            ["We are going to Córdoba in July.", "Córdoba is hot in summer.", "My parents live near Córdoba."],
            "Córdoba in Spain has the famous mosque."),
        new("home", "lake house", "LOCATION", "OBJECT",
            ["We bought the lake house in 2019.", "The lake house needs a new roof.", "The lake house has three bedrooms."],
            "My uncle has a lake house too, on another lake."),
        new("product", "Kindle", "OBJECT", "ORGANIZATION",
            ["I bought a Kindle.", "The Kindle battery lasts weeks.", "I lost the Kindle on the train."],
            "My mother has her own Kindle, an older one."),
        new("vehicle", "Corolla", "OBJECT", "OBJECT",
            ["My Corolla needs new tyres.", "The Corolla passed its inspection.", "I park the Corolla on the street."],
            "My neighbour's Corolla was stolen."),
        new("work", "Dune", "OBJECT", "EVENT",
            ["I am reading Dune.", "Dune has six sequels.", "I lent Dune to Marco."],
            "We watched Dune at the cinema, the new film."),
        new("project", "Atlas", "OBJECT", "EVENT",
            ["Atlas is our migration project.", "Atlas is two weeks late.", "Atlas goes live in March."],
            "At home, Atlas is what we call the kitchen renovation."),
        new("document", "Q3 report", "OBJECT", "OBJECT",
            ["I am writing the Q3 report.", "The Q3 report is due Friday.", "The Q3 report has forty pages."],
            "Finance has its own Q3 report, a separate one."),
    ];

    public static readonly (string Id, string What)[] Situations =
    [
        ("S01-again", "The same thing named in three turns"),
        ("S02-later", "Named again after six unrelated turns"),
        ("S03-session", "Named again in another session a week later"),
        ("S04-namesake", "Two different things with the same name"),
        ("S05-namesake-again", "Two namesakes, then the first one again"),
        ("S06-type", "The same thing given another type in the second turn"),
        ("S07-person-namesake", "The thing, and a person with the same name"),
        ("S08-order", "The turns of S05 in another order"),
        ("S09-one-turn", "Named twice inside one turn"),
        ("S10-times", "The turns of S01 stored with times running backwards"),
    ];

    /// <summary>
    /// Every kind in every situation that applies to it: a changed type (S06) only where a model might give another type,
    /// a person namesake (S07) only for things that are not people. 14 kinds give 134 cases.
    /// </summary>
    public static IEnumerable<Case> Build()
    {
        foreach (var k in Kinds)
        {
            Mention M(int thing, string? type = null, string? name = null) => new(name ?? k.Name, type ?? k.Type, thing);
            Turn A(int i, double at = 0, string session = "s1", string? type = null) => new(k.A[i], [M(0, type)], session, at);
            Turn B(double at = 0) => new(k.B, [M(1)], "s1", at);
            Turn Filler(int i, double at) =>
                new($"Unrelated note {i}: the invoice batch {i} was archived.", [new($"invoice batch {i}", "OBJECT", FillerThing + i)], "s1", at);
            string Id(int s) => $"{k.Id}.{Situations[s].Id}";

            yield return new(Id(0), k.Id, Situations[0].Id, Situations[0].What, 1, [A(0, 0), A(1, 60), A(2, 120)]);
            yield return new(Id(1), k.Id, Situations[1].Id, Situations[1].What, 1,
                [A(0, 0), .. Enumerable.Range(1, 6).Select(i => Filler(i, 60 * i)), A(1, 420)]);
            yield return new(Id(2), k.Id, Situations[2].Id, Situations[2].What, 1, [A(0, 0), A(1, 7 * 86400, "s2")]);
            yield return new(Id(3), k.Id, Situations[3].Id, Situations[3].What, 2, [A(0, 0), B(60)]);
            yield return new(Id(4), k.Id, Situations[4].Id, Situations[4].What, 2, [A(0, 0), B(60), A(1, 120)], LastMustJoinThing: 0);
            if (k.AltType != k.Type)
                yield return new(Id(5), k.Id, Situations[5].Id, Situations[5].What, 1, [A(0, 0), A(1, 60, type: k.AltType), A(2, 120)]);
            if (k.Type != "PERSON")
                yield return new(Id(6), k.Id, Situations[6].Id, Situations[6].What, 2,
                    [A(0, 0), new($"{k.Name} from accounting approved the budget.", [new(k.Name, "PERSON", 2)], "s1", 60)]);
            yield return new(Id(7), k.Id, Situations[7].Id, Situations[7].What, 2, [B(0), A(0, 60), A(1, 120)], LastMustJoinThing: 0);
            yield return new(Id(8), k.Id, Situations[8].Id, Situations[8].What, 1,
                [new(k.A[0] + " " + k.A[1], [M(0), M(0)], "s1", 0)]);
            yield return new(Id(9), k.Id, Situations[9].Id, Situations[9].What, 1, [A(0, 120), A(1, 60), A(2, 0)]);
        }
    }
}
