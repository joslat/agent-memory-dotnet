using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Ask;
using AgentMemory.RouterArena.Contestants;
using AgentMemory.RouterArena.Data;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.RouterArena;

/// <summary>40.72: the router opens any of the eleven doors and the forgetting switch; the referee scores the doors too.</summary>
public sealed class DoorsTests
{
    private const string Owner = "matrix-marta";

    [Fact]
    public void A_searching_door_lets_in_only_its_own_memories()
    {
        var context = Context();

        Ids(context.Open([Door.Semantic])).Should().BeEquivalentTo(["F01", "F02"], "derived facts have their own door");
        Ids(context.Open([Door.Semantic, Door.Derived, Door.Procedural])).Should().BeEquivalentTo(["F01", "F02", "D01", "T01"]);
        context.Open([Door.Semantic, Door.Derived, Door.Procedural]).Searches.Should().Be(2, "derived facts ride in the facts search");
    }

    [Fact]
    public void The_temporal_door_reads_the_facts_as_currently_valid()
    {
        var context = Context();

        Ids(context.Open([Door.Semantic, Door.Temporal])).Should().BeEquivalentTo(["F01"], "the closed fact is not valid now");
        Ids(context.Open([Door.Temporal, Door.Derived])).Should().BeEquivalentTo(["F01", "D01"]);
        context.Open([Door.Semantic, Door.Temporal]).Searches.Should().Be(1, "one facts search, run as currently valid");
    }

    [Fact]
    public void The_reading_doors_let_in_what_is_due_what_was_true_then_and_what_faded()
    {
        var context = Context();

        Ids(context.Open([Door.Prospective])).Should().BeEquivalentTo(["F34"]);
        Ids(context.Open([Door.BiTemporal])).Should().BeEquivalentTo(["F02"]);
        Ids(context.Open([Door.Forgetting])).Should().BeEquivalentTo(["F33"]);
        context.Open([Door.Prospective, Door.BiTemporal, Door.Forgetting]).Searches.Should().Be(3);
    }

    [Fact]
    public void The_working_door_costs_no_search_and_serves_no_need()
    {
        var decision = Context().Open([Door.Working]);

        decision.Admitted.Should().BeEmpty();
        decision.Searches.Should().Be(0);
        decision.Opened.Should().Equal(Door.Working);
    }

    [Fact]
    public void The_referee_scores_the_doors_needed_against_the_doors_opened()
    {
        var item = Item(doors: ["semantic", "temporal"], acceptable: ["working"]);
        var data = Data(item, labelled: true);

        var score = Referee.Score([item], data, new Opens(Door.Semantic, Door.Temporal, Door.Working, Door.Preference), new Dictionary<string, TurnContext>());

        score.Served.Should().Be(1);
        score.DoorRecall.Should().Be(1);
        score.DoorPrecision.Should().Be(0.75, "working is acceptable, preference is wasted");
        score.DoorsExact.Should().Be(0);
        score.Rows.Single().DoorsWasted.Should().Equal("preference");
        score.ByDoor["preference"].Should().Equal(0, 0, 1);
    }

    [Fact]
    public void Without_door_labels_the_doors_needed_are_the_doors_holding_what_is_needed()
    {
        var item = Item(doors: [], acceptable: []) with { Needs = [["F01"], ["T01"]] };
        var data = Data(item, labelled: false);

        var score = Referee.Score([item], data, new Opens(Door.Semantic), new Dictionary<string, TurnContext>());

        score.DoorsLabelled.Should().BeFalse();
        score.Rows.Single().DoorsNeeded.Should().BeEquivalentTo(["semantic", "procedural"]);
        score.DoorRecall.Should().Be(0.5);
        score.Served.Should().Be(0, "the procedure stayed out");
        score.RoutingMisses.Should().Be(1, "the wide search found it");
    }

    [Fact]
    public void The_old_method_reads_every_searching_door_and_no_reading_door()
    {
        var item = Item(doors: ["prospective"], acceptable: []) with { Needs = [["F34"]] };
        var data = Data(item, labelled: true);

        var score = Referee.Score([item], data, new OldMethod(), new Dictionary<string, TurnContext>());

        score.Rows.Single().DoorsOpened.Should().BeEquivalentTo(Doors.Shipped.Select(Doors.Name));
        score.Rows.Single().DoorsShut.Should().Be(1);
        score.Served.Should().Be(0);
        score.SearchesPerTurn.Should().Be(5, "messages, facts, graph, preferences, traces");
    }

    [Theory]
    [InlineData("How do I usually book the train to Valencia?", Door.Procedural)]
    [InlineData("How did Nadia's birthday weekend go?", Door.Reasoning)]
    [InlineData("Where do I live now?", Door.Temporal)]
    [InlineData("Where did I live before Lyon?", Door.BiTemporal)]
    [InlineData("Is anything due this week?", Door.Prospective)]
    [InlineData("How many languages do I speak?", Door.Derived)]
    [InlineData("Do you still remember my trumpet?", Door.Forgetting)]
    [InlineData("What do you know about me?", Door.Working)]
    [InlineData("¿Cuántos idiomas hablo?", Door.Derived)]
    [InlineData("Wo habe ich früher gewohnt?", Door.BiTemporal)]
    public void Rules_v2_opens_a_door_on_its_cue_words(string text, Door door) =>
        RulesV2.Cued(new MatrixItem { Id = "m", Text = text }).Should().Contain(door);

    [Fact]
    public void JEV_one_for_all_opens_every_door_its_answer_reaches_and_reports_its_own_time()
    {
        var item = Item(doors: ["semantic", "temporal"], acceptable: []);
        var answer = new JevAnswer(new Dictionary<string, double> { ["semantic"] = 0.9, ["temporal"] = 0.7, ["preference"] = 0.2 }, 1, 0.3, 0);
        var data = Data(item, labelled: true, jev: new() { ["doors"] = new Dictionary<string, JevAnswer?> { [item.Id] = answer } });

        var decision = new JevDoors(0.5).Decide(item, new TurnContext(data.Records[item.Id], data));

        decision.Opened.Should().BeEquivalentTo([Door.Semantic, Door.Temporal]);
        decision.Admitted.Should().BeEquivalentTo(["F01"]);
        decision.ModelSeconds.Should().Be(0.3);
        new JevDoors(0.5, new Dictionary<Door, double> { [Door.Temporal] = 0.8 }).Decide(item, new TurnContext(data.Records[item.Id], data))
            .Opened.Should().BeEquivalentTo([Door.Semantic], "a door's own threshold overrides the default");
    }

    [Fact]
    public void A_turn_JEV_never_answered_falls_back_to_what_the_old_method_reads()
    {
        var item = Item(doors: ["semantic"], acceptable: []);
        var data = Data(item, labelled: true, jev: new() { ["doors"] = new Dictionary<string, JevAnswer?> { [item.Id] = null } });

        var decision = new JevDoors().Decide(item, new TurnContext(data.Records[item.Id], data));

        decision.Lane.Should().Be("jev-failed");
        decision.Opened.Should().BeEquivalentTo(Doors.Shipped);
    }

    [Fact]
    public void JEVs_reply_is_read_as_P_yes_per_question_with_the_model_that_answered()
    {
        var reply = JevClient.Parse("""
            {"id":"r1","model":"jev-1.13.0","answers":{"semantic":{"type":"noul","noul":0.91},"temporal":{"type":"noul","noul":0.12}},
             "usage":{"input_tokens":120,"output_tokens":2,"cost":0.00003}}
            """, ["semantic", "temporal"], 0.25);

        reply.Model.Should().Be("jev-1.13.0");
        reply.Yes.Should().Equal(new Dictionary<string, double> { ["semantic"] = 0.91, ["temporal"] = 0.12 });
        reply.Cost.Should().Be(0.00003);
    }

    [Fact]
    public void A_door_name_parses_as_the_matrix_writes_it() =>
        new[] { "entity-graph", "bi-temporal", "BiTemporal", "entity_graph", "forgetting" }.Select(Doors.Parse)
            .Should().Equal(Door.EntityGraph, Door.BiTemporal, Door.BiTemporal, Door.EntityGraph, Door.Forgetting);

    private sealed class Opens(params Door[] doors) : IContestant
    {
        public string Family => "test";

        public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?>();

        public Decision Decide(MatrixItem item, TurnContext context) => context.Open(doors);
    }

    private static IEnumerable<string> Ids(Decision decision) => decision.Admitted;

    private static MatrixItem Item(IReadOnlyList<string> doors, IReadOnlyList<string> acceptable) => new()
    {
        Id = "m2:t:01", Text = "Where do I live now?", Needs = [["F01"]], DoorNames = doors, DoorsAcceptableNames = acceptable,
    };

    private static TurnContext Context()
    {
        var item = Item(["semantic"], []);
        var data = Data(item, labelled: true);
        return new TurnContext(data.Records[item.Id], data);
    }

    private static ArenaData Data(MatrixItem item, bool labelled, Dictionary<string, IReadOnlyDictionary<string, JevAnswer?>>? jev = null)
    {
        static Hit H(string key, double? score, int rank, string? kind = null) => new(key, score, rank, Owner, kind);
        static Section Facts(params Hit[] facts) => new(facts, [], [], [], [], [], null);
        var today = new Section(
            [H("Marta | lives in | Lyon", 0.8, 1), H("Marta | lived in | Valencia", 0.75, 2), H("Marta | count_of:speaks | 3", 0.72, 3, "derived")],
            [H("Lyon", 0.71, 1)], [], [], [], [], null)
        {
            Traces = [H("Book the train", 0.9, 1, "procedure")],
        };
        var record = new TurnRecord(item.Id, item.Text, today, today, null, Doors: new Dictionary<string, Section>
        {
            ["temporal"] = Facts(H("Marta | lives in | Lyon", 0.8, 1), H("Marta | count_of:speaks | 3", 0.72, 2, "derived")),
            ["prospective"] = Facts() with { Due = [H("Marta | has to renew | her passport", null, 1)] },
            ["faded"] = Facts(H("Marta | played | the trumpet", null, 1, "faded")),
            ["asOf:2020-06-01"] = Facts(H("Marta | lived in | Valencia", 0.75, 1)),
        });
        var world = new WorldIndex(
        [
            ("F01", "fact", "Marta | lives in | Lyon", ""), ("F02", "fact", "Marta | lived in | Valencia", ""),
            ("D01", "fact", "Marta | count_of:speaks | 3", "derived by the accountant"), ("E02", "entity", "Lyon", ""),
            ("T01", "trace", "Book the train", "procedure, booked"), ("F34", "fact", "Marta | has to renew | her passport", ""),
            ("F33", "fact", "Marta | played | the trumpet", ""),
        ], new Dictionary<string, IReadOnlyList<string>>(), owner: "marta");
        var caps = new Caps(10, 5, 5, 5, 5, 6, 0.7);
        return new ArenaData
        {
            Matrix = new Matrix([item], new string('0', 64), "matrix.json") { DoorsLabelled = labelled },
            Recording = new Recording("routing-recording/2", new Source("w", ""), new Source("m", ""), "test", DateTimeOffset.UnixEpoch,
                "marta", "marta-now", caps, [record]),
            RecordingSha256 = new string('0', 64),
            World = world,
            Records = new Dictionary<string, TurnRecord> { [item.Id] = record },
            ModelAnswers = new Dictionary<string, IReadOnlyList<string>?>(),
            Embeddings = new Dictionary<string, IReadOnlyDictionary<string, double[]>>(),
            Train = [],
            Jev = jev ?? [],
            InputShas = new Dictionary<string, string>(),
        };
    }
}
