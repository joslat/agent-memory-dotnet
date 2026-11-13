using System.Text.Json;
using AgentMemory.Scenarios.PublicGold;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ILC-23: the independent identity gold benchmark. Scorer, ECB+ and ConEL-2 adapters on fixtures written here (our own
/// sentences, no dataset text); no model, no dataset download.
/// </summary>
public sealed class IdentityGoldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "identity-gold-tests-" + Guid.NewGuid().ToString("N"));

    public IdentityGoldTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── Scorer ────────────────────────────────────────────────────────────────

    private static readonly string[] Statements = ["We launch on Friday.", "The wedding is in Bariloche.", "The launch moved."];

    private static IdentityGold Gold(string match = IdentityGold.Substring, Dictionary<string, string>? partitions = null) => new(
        "test", "sha",
        new Dictionary<string, GoldGroup> { ["A"] = new("EVENT", ["launch"]), ["B"] = new("EVENT", ["wedding"]), ["P"] = new("PERSON", ["ann"]) },
        new Dictionary<string, IReadOnlyList<string>> { ["1"] = ["A"], ["2"] = ["B", "P"], ["3"] = ["A"] },
        match, partitions);

    private static object Decision(string id, int status, string name, string type, int statement, string? confirmed = null, string[]? pending = null) => new
    {
        DecisionId = "identity-decision-" + id,
        Observation = new { EntityId = "identity-observation-" + id, Name = name, Type = type },
        Context = new { SourceMessageIds = new[] { "m" + statement }, Sources = new[] { new { SourceId = "m" + statement, Text = Statements[statement - 1] } } },
        Status = status,
        ConfirmedEntityId = confirmed is null ? null : "identity-observation-" + confirmed,
        PendingEntityIds = (pending ?? []).Select(p => "identity-observation-" + p).ToArray(),
    };

    private static JsonElement DecisionStore(params object[] decisions) =>
        JsonSerializer.SerializeToElement(new { identity = new { decisions } });

    [Fact]
    public void Scorer_finds_no_merge_and_no_split_when_follow_ups_are_confirmed()
    {
        var score = IdentityGoldScorer.Score(Gold(), Statements, DecisionStore(
            Decision("1", 2, "Launch", "EVENT", 1), Decision("2", 0, "launch", "EVENT", 3, confirmed: "1"),
            Decision("3", 2, "Wedding", "EVENT", 2)), IdentityGoldScorer.DecisionsView);

        score.FalseMerges.Should().Be(0);
        score.ExtraIdentities.Should().Be(0);
        score.Identities.Should().Be(2);
        score.UnlabelledMentions.Should().Be(0);
    }

    [Fact]
    public void Scorer_counts_a_split_event_and_a_wrong_merge_like_the_python_metric()
    {
        var split = IdentityGoldScorer.Score(Gold(), Statements, DecisionStore(
            Decision("1", 2, "Launch", "EVENT", 1), Decision("2", 2, "launch", "EVENT", 3)), IdentityGoldScorer.DecisionsView);
        split.ExtraIdentities.Should().Be(1);
        split.EventExtraIdentities.Should().Be(1);
        split.SplitGroups.Should().ContainKey("A").WhoseValue.Should().Be(2);

        // "Wedding launch" said in statement 2 can only be the wedding there (the launch is not mentioned in it), but it
        // was confirmed onto the launch's identity: one identity holding two gold groups.
        var merged = IdentityGoldScorer.Score(Gold(), Statements, DecisionStore(
            Decision("1", 2, "Launch", "EVENT", 1), Decision("3", 0, "Wedding launch", "EVENT", 2, confirmed: "1")), IdentityGoldScorer.DecisionsView);
        merged.FalseMerges.Should().Be(1);
        merged.FalseMergeDetail.Should().ContainKey("identity-observation-1").WhoseValue.Should().Equal("A", "B");
    }

    [Fact]
    public void Label_requires_the_kind_the_statement_and_exactly_one_hit()
    {
        var gold = Gold();
        IdentityGoldScorer.Label(gold, "Launch", "EVENT", [1]).Should().Be("A");
        IdentityGoldScorer.Label(gold, "Launch", "PERSON", [1]).Should().BeNull("the kind must match");
        IdentityGoldScorer.Label(gold, "Launch", "EVENT", [2]).Should().BeNull("statement 2 does not mention the launch");
        IdentityGoldScorer.Label(gold, "Launch", "EVENT", []).Should().BeNull("no statement, no label");
        IdentityGoldScorer.Label(gold, "Wedding launch", "EVENT", [1, 2]).Should().BeNull("two groups hit");

        var any = gold with { Groups = new Dictionary<string, GoldGroup> { ["K"] = new(IdentityGold.AnyKind, ["kid rock"]) },
            StatementGroups = new Dictionary<string, IReadOnlyList<string>> { ["1"] = ["K"] } };
        IdentityGoldScorer.Label(any, "Kid Rock", "PERSON", [1]).Should().Be("K");
        IdentityGoldScorer.Label(any, "Kid Rock's tour", "EVENT", [1]).Should().Be("K");
    }

    [Fact]
    public void Word_prefix_matching_starts_at_a_word_and_substring_matching_keeps_python_parity()
    {
        var groups = new Dictionary<string, GoldGroup> { ["E"] = new("EVENT", ["enter", "check"]) };
        var statementGroups = new Dictionary<string, IReadOnlyList<string>> { ["1"] = ["E"] };
        var prefix = new IdentityGold("t", "s", groups, statementGroups, IdentityGold.WordPrefix);
        var substring = prefix with { Match = IdentityGold.Substring };

        IdentityGoldScorer.Label(prefix, "Stay at Betty Ford Center", "EVENT", [1]).Should().BeNull("'enter' is inside 'Center'");
        IdentityGoldScorer.Label(substring, "Stay at Betty Ford Center", "EVENT", [1]).Should().Be("E");
        IdentityGoldScorer.Label(prefix, "Rehab check-in", "EVENT", [1]).Should().Be("E");
        IdentityGoldScorer.Label(prefix, "Entered rehab", "EVENT", [1]).Should().Be("E");
    }

    [Fact]
    public void Pending_proposals_count_as_splits_until_accepted()
    {
        var score = IdentityGoldScorer.Score(Gold(), Statements, DecisionStore(
            Decision("1", 2, "Launch", "EVENT", 1), Decision("2", 1, "launch", "EVENT", 3, pending: ["1"])), IdentityGoldScorer.DecisionsView);

        score.PendingDecisions.Should().Be(1);
        score.ExtraIdentities.Should().Be(1);
        score.ExtraIdentitiesIfPendingAccepted.Should().Be(0);
    }

    [Fact]
    public void Entities_view_scores_names_and_aliases_per_source_and_counts_same_as_links()
    {
        var store = JsonSerializer.SerializeToElement(new
        {
            entities = new
            {
                entities = new object[]
                {
                    new { id = "e1", name = "Launch", type = "EVENT", aliases = new[] { "wedding" }, sources = new[] { Statements[0], Statements[1] } },
                    new { id = "e2", name = "Ann", type = "PERSON", aliases = Array.Empty<string>(), sources = new[] { Statements[1] } },
                    new { id = "e3", name = "launch", type = "EVENT", aliases = Array.Empty<string>(), sources = new[] { Statements[2] } },
                },
                sameAs = new[] { new { a = "e1", b = "e3" } },
            },
        });

        var score = IdentityGoldScorer.Score(Gold(), Statements, store, IdentityGoldScorer.EntitiesView);

        score.FalseMerges.Should().Be(1, "e1 holds the launch (statement 1) and the wedding (statement 2)");
        score.SplitGroups.Should().ContainKey("A").WhoseValue.Should().Be(2);
        score.SameAsLinks.Should().Be(1);
        score.UnlabelledMentions.Should().Be(2, "'Launch' in statement 2 and 'wedding' in statement 1 match nothing there");
    }

    [Fact]
    public void An_event_identity_across_two_similar_events_of_one_topic_is_listed_for_review()
    {
        var partitions = new Dictionary<string, string> { ["1"] = "1/ecb", ["2"] = "1/ecbplus", ["3"] = "2/ecb" };
        var gold = Gold(partitions: partitions);

        var crossed = IdentityGoldScorer.Score(gold, Statements, DecisionStore(
            Decision("1", 2, "Rehab stay", "EVENT", 1), Decision("2", 0, "Rehab stay", "EVENT", 2, confirmed: "1")), IdentityGoldScorer.DecisionsView);
        crossed.CrossPartitionEventIdentities.Should().ContainSingle().Which.Partitions.Should().Equal("1/ecb", "1/ecbplus");

        var otherTopic = IdentityGoldScorer.Score(gold, Statements, DecisionStore(
            Decision("1", 2, "Rehab stay", "EVENT", 1), Decision("2", 0, "Rehab stay", "EVENT", 3, confirmed: "1")), IdentityGoldScorer.DecisionsView);
        otherTopic.CrossPartitionEventIdentities.Should().BeEmpty("different topics are not similar events");

        var person = IdentityGoldScorer.Score(gold, Statements, DecisionStore(
            Decision("1", 2, "Ann", "PERSON", 1), Decision("2", 0, "Ann", "PERSON", 2, confirmed: "1")), IdentityGoldScorer.DecisionsView);
        person.CrossPartitionEventIdentities.Should().BeEmpty("only events are checked across similar events");
    }

    [Fact]
    public void Gold_round_trips_the_bariloche_json_shape()
    {
        var path = Path.Combine(_root, "gold.json");
        File.WriteAllText(path, """
            {"version":"v1","frozenAt":"x","scenarioSha256":"abc","purpose":"p","rules":["r"],
             "groups":{"GO-LIVE":{"kind":"EVENT","keywords":["go-live","cutover"]}},
             "statementGroups":{"2":["GO-LIVE"]}}
            """);
        var gold = IdentityGold.Load(path);
        gold.Match.Should().Be(IdentityGold.Substring);
        gold.Groups["GO-LIVE"].Keywords.Should().Equal("go-live", "cutover");
        gold.StatementGroups["2"].Should().Equal("GO-LIVE");
        IdentityGold.Load(Save(gold)).Should().BeEquivalentTo(gold);
    }

    private string Save(IdentityGold gold)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, gold.ToJson());
        return path;
    }

    // ── ECB+ adapter ─────────────────────────────────────────────────────────

    private sealed record Mention(string Tag, int[] Tokens, string Instance);
    private sealed record Sentence(int Number, string[] Tokens, Mention[] Mentions);

    private void EcbDocument(string topic, string file, Dictionary<string, (string Tag, string Descriptor)> instances, params Sentence[] sentences)
    {
        var folder = Path.Combine(_root, "ECB+", topic);
        Directory.CreateDirectory(folder);
        var tokens = new List<string>();
        var markables = new List<string>();
        var links = new Dictionary<string, List<int>>();
        int t = 0, m = 0;
        foreach (var sentence in sentences)
        {
            var first = t + 1;
            for (var i = 0; i < sentence.Tokens.Length; i++)
                tokens.Add($"<token t_id=\"{++t}\" sentence=\"{sentence.Number}\" number=\"{i}\">{sentence.Tokens[i]}</token>");
            foreach (var mention in sentence.Mentions)
            {
                markables.Add($"<{mention.Tag} m_id=\"{++m}\">" + string.Concat(mention.Tokens.Select(k => $"<token_anchor t_id=\"{first + k}\"/>")) + $"</{mention.Tag}>");
                (links.TryGetValue(mention.Instance, out var list) ? list : links[mention.Instance] = []).Add(m);
            }
        }
        var relations = new List<string>();
        var r = 0;
        foreach (var (instance, sources) in links)
        {
            var (tag, descriptor) = instances[instance];
            markables.Add($"<{tag} m_id=\"{++m}\" RELATED_TO=\"\" TAG_DESCRIPTOR=\"{descriptor}\" instance_id=\"{instance}\" />");
            relations.Add($"<CROSS_DOC_COREF r_id=\"{++r}\" note=\"{instance}\">" + string.Concat(sources.Select(s => $"<source m_id=\"{s}\" />")) + $"<target m_id=\"{m}\" /></CROSS_DOC_COREF>");
        }
        File.WriteAllText(Path.Combine(folder, file), $"<Document doc_name=\"{file}\" doc_id=\"D{file}\">\n{string.Join('\n', tokens)}\n<Markables>\n{string.Join('\n', markables)}\n</Markables>\n<Relations>\n{string.Join('\n', relations)}\n</Relations>\n</Document>");
    }

    private void EcbFixture()
    {
        var instances = new Dictionary<string, (string, string)>
        {
            ["ACT1"] = ("ACTION_OCCURRENCE", "t1a_ann_checks_in"), ["ACT2"] = ("ACTION_OCCURRENCE", "t1b_bea_checks_in"),
            ["ACT9"] = ("ACTION_OCCURRENCE", "t1a_ann_sings"), ["REP1"] = ("ACTION_REPORTING", "t1a_reports"),
            ["HUM1"] = ("HUMAN_PART_PER", "t1a_ann_lee"), ["HUM2"] = ("HUMAN_PART_PER", "t1b_bea_cruz"), ["LOC1"] = ("LOC_FAC", "t1a_clinic"),
        };
        string[] Ann(string verb) => ["Ann", "Lee", verb, "into", "the", "clinic", ",", "staff", "said", "."];
        Mention[] annMentions = [new("HUMAN_PART_PER", [0, 1], "HUM1"), new("ACTION_OCCURRENCE", [2], "ACT1"), new("LOC_FAC", [5], "LOC1"), new("ACTION_REPORTING", [8], "REP1")];
        EcbDocument("1", "1_1ecb.xml", instances, new Sentence(0, Ann("checked"), annMentions), new Sentence(1, ["She", "sang", "."], [new("ACTION_OCCURRENCE", [1], "ACT9")]));
        EcbDocument("1", "1_2ecb.xml", instances, new Sentence(0, Ann("entered"), annMentions));
        EcbDocument("1", "1_3ecb.xml", instances, new Sentence(0, ["No", "news", "."], []));
        string[] Bea = ["Bea", "Cruz", "checked", "into", "rehab", "."];
        Mention[] beaMentions = [new("HUMAN_PART_PER", [0, 1], "HUM2"), new("ACTION_OCCURRENCE", [2], "ACT2")];
        EcbDocument("1", "1_1ecbplus.xml", instances, new Sentence(0, ["http://example.invalid/a"], []), new Sentence(1, Bea, beaMentions));
        EcbDocument("1", "1_2ecbplus.xml", instances, new Sentence(0, ["http://example.invalid/b"], []), new Sentence(1, Bea, beaMentions));
        File.WriteAllText(Path.Combine(_root, "ECBplus_coreference_sentences.csv"),
            "Topic,File,Sentence Number\n1,1ecb,0\n1,1ecb,1\n1,2ecb,0\n1,3ecb,0\n1,1ecbplus,0\n1,1ecbplus,1\n1,2ecbplus,1\n");
    }

    [Fact]
    public void Ecb_selection_takes_the_seminal_event_per_subtopic_and_its_documents_by_rule()
    {
        EcbFixture();
        var slice = EcbPlusAdapter.Select(_root, topics: 1, documentsPerEvent: 2);

        slice.Version.Should().Be(EcbPlusAdapter.SliceVersion);
        var topic = slice.Topics.Should().ContainSingle().Subject;
        topic.Subtopics.Select(s => s.Subtopic).Should().Equal("ecb", "ecbplus");
        topic.Subtopics[0].Seminal.Should().Be("ACT1", "ACT1 is in two documents, ACT9 in one");
        topic.Subtopics[0].Descriptor.Should().Be("t1a_ann_checks_in");
        topic.Subtopics[0].Documents.Select(d => d.File).Should().Equal("1_1ecb.xml", "1_2ecb.xml");
        topic.Subtopics[0].Documents[0].Sentences.Select(s => s.Sentence).Should().Equal(0, 1);
        topic.Subtopics[1].Seminal.Should().Be("ACT2");
        topic.Subtopics[1].Documents[0].Sentences.Select(s => s.Sentence).Should().Equal([1], "sentence 0 of an ecbplus file is its source URL");
    }

    [Fact]
    public void Ecb_build_interleaves_similar_events_and_derives_the_gold_from_the_annotations()
    {
        EcbFixture();
        var (scenario, gold) = EcbPlusAdapter.Build(EcbPlusAdapter.Select(_root, topics: 1, documentsPerEvent: 2), _root);

        // Conversion v2: a personal memory stores nothing of general news (igb-ecbplus-one-01 stored 0 facts from verbatim
        // sentences), so every sentence is said as part of the user's work: one fixed frame, the source words unchanged.
        const string frame = "I'm covering this story at work: ";
        EcbPlusAdapter.Framing.Should().Be(frame);
        scenario.Sessions.Should().HaveCount(4);
        scenario.Sessions.Select(s => s.Messages[0]).Should().Equal(
            frame + "Ann Lee checked into the clinic, staff said.", frame + "Bea Cruz checked into rehab.",
            frame + "Ann Lee entered into the clinic, staff said.", frame + "Bea Cruz checked into rehab.");
        scenario.Sessions[0].Messages.Should().Equal(frame + "Ann Lee checked into the clinic, staff said.", frame + "She sang.");
        scenario.Sessions.Select(s => s.Date).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        scenario.Questions.Should().BeEmpty();
        scenario.Sha256.Should().NotBeNullOrEmpty().And.Be(gold.ScenarioSha256);

        gold.Match.Should().Be(IdentityGold.WordPrefix);
        gold.Groups.Keys.Should().BeEquivalentTo(["ACT1", "ACT2", "ACT9", "HUM1", "HUM2", "LOC1"], "reporting acts are not identities");
        gold.Groups["ACT1"].Kind.Should().Be("EVENT");
        gold.Groups["HUM1"].Kind.Should().Be("PERSON");
        gold.Groups["LOC1"].Kind.Should().Be("LOCATION");
        gold.Groups["ACT1"].Keywords.Should().Contain(["check", "enter"]).And.NotContain(["into", "the", "t1a"]);
        gold.Groups["HUM1"].Keywords.Should().Contain(["ann lee", "ann", "lee"]);
        gold.StatementGroups["1"].Should().BeEquivalentTo(["HUM1", "ACT1", "LOC1"]);
        gold.StatementGroups["2"].Should().Equal("ACT9");
        gold.StatementPartitions!["1"].Should().Be("1/ecb");
        gold.StatementPartitions["3"].Should().Be("1/ecbplus");

        var one = EcbPlusAdapter.Build(EcbPlusAdapter.Select(_root, topics: 1, documentsPerEvent: 2), _root, sessions: 1).Scenario;
        one.Sessions.Should().HaveCount(1);
    }

    [Fact]
    public void Ecb_build_refuses_a_dataset_whose_sentence_changed()
    {
        EcbFixture();
        var slice = EcbPlusAdapter.Select(_root, topics: 1, documentsPerEvent: 2);
        var file = Path.Combine(_root, "ECB+", "1", "1_2ecb.xml");
        File.WriteAllText(file, File.ReadAllText(file).Replace(">entered<", ">exited<", StringComparison.Ordinal));

        var build = () => EcbPlusAdapter.Build(slice, _root);
        build.Should().Throw<InvalidDataException>().WithMessage("*1_2ecb.xml*");
    }

    [Fact]
    public void Ecb_slice_round_trips_and_detokenizes_news_punctuation()
    {
        EcbFixture();
        var slice = EcbPlusAdapter.Select(_root, topics: 1, documentsPerEvent: 2);
        EcbPlusAdapter.LoadSlice(Write(EcbPlusAdapter.SliceJson(slice))).Should().BeEquivalentTo(slice);
        EcbPlusAdapter.Detokenize(["``", "Hi", "''", ",", "she", "said", "(", "to", "us", ")", "and", "it", "'s", "fine", "."])
            .Should().Be("\"Hi\", she said (to us) and it's fine.");
    }

    // ── ConEL-2 adapter ──────────────────────────────────────────────────────

    private void ConElFixture() => File.WriteAllText(Path.Combine(_root, "ConEL22_EL_Test.json"), """
        [
          {"dialogue_id":"1","turns":[
            {"speaker":"USER","utterance":"My favorite singer is Kid Rock!","turn_number":0,
             "el_annotations":[{"mention":"Kid Rock","entity":"Kid_Rock","span":[22,30]}],
             "personal_entity_annotations":[{"personal_entity_mention":"My favorite singer","explicit_entity_mention":"Kid Rock","entity":"Kid_Rock"}]},
            {"speaker":"SYSTEM","utterance":"Mine too!","turn_number":1},
            {"speaker":"USER","utterance":"I like kid rock and rock music.","turn_number":2,
             "el_annotations":[{"mention":"kid rock","entity":"Kid_Rock","span":[7,15]},{"mention":"rock music","entity":"Rock_music","span":[20,30]}],
             "personal_entity_annotations":[]}]},
          {"dialogue_id":"2","turns":[
            {"speaker":"USER","utterance":"Hello there.","turn_number":0,"el_annotations":[],"personal_entity_annotations":[]}]},
          {"dialogue_id":"3","turns":[
            {"speaker":"USER","utterance":"My dog Rex loves the beach.","turn_number":0,
             "el_annotations":[{"mention":"beach","entity":"Beach (geography)","span":[21,26]}],
             "personal_entity_annotations":[{"personal_entity_mention":"My dog","explicit_entity_mention":"Rex","entity":null}]}]}
        ]
        """);

    [Fact]
    public void Conel_selection_takes_dialogues_with_personal_entities_in_file_order()
    {
        ConElFixture();
        var slice = ConElAdapter.Select(_root, dialogues: 5);

        slice.Version.Should().Be(ConElAdapter.SliceVersion);
        slice.Dialogues.Select(d => d.DialogueId).Should().Equal("1", "3");
        slice.Dialogues[0].Turns.Select(t => t.Turn).Should().Equal([0, 2], "user turns only");
        ConElAdapter.LoadSlice(Write(ConElAdapter.SliceJson(slice))).Should().BeEquivalentTo(slice);
    }

    [Fact]
    public void Conel_build_keeps_user_turns_and_derives_entity_groups()
    {
        ConElFixture();
        var (scenario, gold) = ConElAdapter.Build(ConElAdapter.Select(_root, dialogues: 5), _root);

        scenario.Sessions.Should().HaveCount(2);
        scenario.Sessions[0].Messages.Should().Equal("My favorite singer is Kid Rock!", "I like kid rock and rock music.");
        scenario.Sha256.Should().Be(gold.ScenarioSha256);
        gold.Groups.Keys.Should().BeEquivalentTo(["Kid_Rock", "Rock_music", "Beach (geography)"]);
        gold.Groups["Kid_Rock"].Kind.Should().Be(IdentityGold.AnyKind);
        gold.Groups["Kid_Rock"].Keywords.Should().Contain("kid rock");
        gold.Groups["Beach (geography)"].Keywords.Should().Contain("beach").And.NotContain("beach (geography)");
        gold.StatementGroups["2"].Should().BeEquivalentTo(["Kid_Rock", "Rock_music"]);
        gold.StatementPartitions.Should().BeNull();
    }

    [Fact]
    public void Conel_build_refuses_a_changed_utterance()
    {
        ConElFixture();
        var slice = ConElAdapter.Select(_root, dialogues: 5);
        var file = Path.Combine(_root, "ConEL22_EL_Test.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("loves the beach", "hates the beach", StringComparison.Ordinal));

        var build = () => ConElAdapter.Build(slice, _root);
        build.Should().Throw<InvalidDataException>().WithMessage("*dialogue 3*");
    }

    // ── Dataset location ─────────────────────────────────────────────────────

    [Fact]
    public void Dataset_location_prefers_the_argument_then_the_environment_then_the_default_folder()
    {
        var defaultFolder = Path.Combine(_root, "artifacts", "datasets", "ecbplus");
        PublicGoldSet.ResolveDataset("ecbplus", null, _ => null, _root).Should().BeNull("nothing is there yet");
        Directory.CreateDirectory(defaultFolder);
        PublicGoldSet.ResolveDataset("ecbplus", null, _ => null, _root).Should().Be(defaultFolder);
        PublicGoldSet.ResolveDataset("ecbplus", null, name => name == "ECBPLUS_DATASET" ? "env" : null, _root).Should().Be("env");
        PublicGoldSet.ResolveDataset("ecbplus", "arg", name => "env", _root).Should().Be("arg");
        PublicGoldSet.ResolveDataset("conel2", null, name => name == "CONEL2_DATASET" ? "c" : null, _root).Should().Be("c");
        var unknown = () => PublicGoldSet.ResolveDataset("weps", null, _ => null, _root);
        unknown.Should().Throw<ArgumentException>();
    }

    private string Write(string json)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
