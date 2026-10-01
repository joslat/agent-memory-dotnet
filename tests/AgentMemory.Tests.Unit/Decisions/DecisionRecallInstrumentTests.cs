using FluentAssertions;
using AgentMemory.Decisions;

namespace AgentMemory.Tests.Unit.Decisions;

/// <summary>
/// PLAN 40.16: the instrument that decides whether decision memory lives. A scoring bug here would decide the kill test,
/// so its chunking, placement and grading are pinned before it runs.
/// </summary>
public sealed class DecisionRecallInstrumentTests
{
    private const string Adr = """
        ---
        status: superseded by [ADR-0062](0062-open-api-payload.md)
        date: 2023-08-15
        ---
        # Dynamic payload

        ## Context and Problem Statement

        Something to solve.

        ```python
        # not a heading
        x = 1
        ```

        ## Decision Outcome

        Chosen option: configuration.

        ## Empty

        ## Links
        """;

    private const string Changelog = """
        # Changelog

        ## [Unreleased]

        - **Not a decision yet.** Pending.

        ## [1.6.0] - 2026-09-28

        ### Changed

        - **Extract from user messages only, by default.** The reply is no longer extracted.
          Second line of the same entry.
        - Plain entry without a bold title. More text.

        ## [1.5.0] - 2026-08-26

        ### Added

        - **Extract from user messages only (opt-in).** Off by default.
        """;

    [Fact]
    public void An_ADR_is_split_by_section_with_its_date_and_status_and_a_code_comment_is_not_a_heading()
    {
        var chunks = DecisionCorpus.FromAdr(Adr, "docs/decisions/0006-payload.md", "A", 0).ToList();

        chunks.Select(c => c.Heading).Should().Equal("Context and Problem Statement", "Decision Outcome");
        chunks[0].Text.Should().Contain("# not a heading", "a comment in a code sample stays in its section");
        chunks[1].Date.Should().Be("2023-08-15");
        chunks[1].Text.Should().Contain("status: superseded by").And.Contain("Chosen option: configuration.");
        chunks.Select(c => c.Id).Should().Equal("A1", "A2");
    }

    [Fact]
    public void Release_notes_give_one_chunk_per_dated_entry_and_skip_Unreleased()
    {
        var chunks = DecisionCorpus.FromChangelog(Changelog, "CHANGELOG.md", "C");

        chunks.Select(c => c.Heading).Should().Equal(
            "## [1.6.0] - 2026-09-28 > ### Changed > Extract from user messages only, by default.",
            "## [1.6.0] - 2026-09-28 > ### Changed > Plain entry without a bold title.",
            "## [1.5.0] - 2026-08-26 > ### Added > Extract from user messages only (opt-in).");
        chunks[0].Text.Should().Contain("Second line of the same entry.");
        chunks[0].Date.Should().Be("2026-09-28");
    }

    [Fact]
    public void ADR_templates_are_skipped_but_an_ADR_about_templates_is_not() =>
        new[] { "adr-template.md", "adr-short-template.md", "0016-custom-prompt-template-formats.md" }
            .Select(DecisionCorpus.IsTemplate).Should().Equal(true, true, false);

    private static DecisionFixtureFile Fixture() => new(
    [
        new FixtureChain("C-01", "C", "user-only extraction", "implicit", false,
        [
            new FixtureDecision("C-01-1", "CHANGELOG.md", "## [1.5.0] - 2026-08-26 > ### Added > Extract from user messages only (opt-in).", "2026-08-26", "", ""),
            new FixtureDecision("C-01-2", "CHANGELOG.md", "## [1.6.0] - 2026-09-28 > ### Changed > Extract from user messages only, by default.", "2026-09-28", "", ""),
        ],
        [new FixtureReplacement("C-01-2", "C-01-1")],
        [
            new FixtureQuestion("in_force_now", "Is extraction user-only?", null, ["C-01-2"]),
            new FixtureQuestion("in_force_on_date", "On 2026-09-01?", "2026-09-01", ["C-01-1"]),
            new FixtureQuestion("what_replaced", "What replaced the opt-in?", null, ["C-01-2"]),
        ]),
    ], [new FixtureAbstention("C", "Does it support Klingon?")]);

    [Fact]
    public void A_release_decision_is_placed_on_its_own_release_entry_only()
    {
        var located = DecisionLabels.Locate(Fixture(), DecisionCorpus.FromChangelog(Changelog, "CHANGELOG.md", "C"));

        located["C-01-1"].Should().Equal("C3");
        located["C-01-2"].Should().Equal("C1");
    }

    /// <summary>
    /// Stage-2 finding: an answer citing another section of the right ADR names the right decision. An ADR decision is the
    /// whole document, unless an addendum in the same file replaces it within its chain; then the section counts.
    /// </summary>
    [Fact]
    public void An_ADR_decision_is_its_document_unless_its_chain_has_another_decision_in_the_same_file()
    {
        var chunks = DecisionCorpus.FromAdr(Adr, "docs/decisions/0006-payload.md", "A", 0).ToList();
        FixtureChain Chain(params FixtureDecision[] decisions) => new("A-01", "A", "t", "explicit", false, decisions, [], []);
        FixtureDecision D(string id, string heading) => new(id, "docs/decisions/0006-payload.md", heading, "2023-08-15", "", "");

        DecisionLabels.Locate(new DecisionFixtureFile([Chain(D("A-01-1", "Decision Outcome"))], []), chunks)["A-01-1"]
            .Should().Equal("A1", "A2");
        var addendum = DecisionLabels.Locate(new DecisionFixtureFile(
            [Chain(D("A-01-1", "Context and Problem Statement"), D("A-01-2", "Decision Outcome"))], []), chunks);
        addendum["A-01-1"].Should().Equal("A1");
        addendum["A-01-2"].Should().Equal("A2");
    }

    [Fact]
    public void What_was_replaced_depends_on_when_the_question_is_asked()
    {
        var questions = DecisionLabels.Questions(Fixture());

        questions[0].ReplacedAtQuestionTime.Should().Equal("C-01-1");
        questions[1].ReplacedAtQuestionTime.Should().BeEmpty("on 2026-09-01 the opt-in was still in force");
        questions.Last().Kind.Should().Be("abstention");
    }

    [Fact]
    public void Grading_reads_the_citations_and_a_replaced_decision_cited_as_in_force_is_a_leak()
    {
        var located = DecisionLabels.Locate(Fixture(), DecisionCorpus.FromChangelog(Changelog, "CHANGELOG.md", "C"));
        var q = DecisionLabels.Questions(Fixture());

        DecisionGrading.Grade(q[0], false, ["C1"], "", located).Should().Match<DecisionAnswer>(a => a.Correct && !a.Leak);
        DecisionGrading.Grade(q[0], false, ["C1", "C3"], "", located).Should().Match<DecisionAnswer>(a => !a.Correct && a.Leak);
        DecisionGrading.Grade(q[0], false, ["C3"], "", located).Should().Match<DecisionAnswer>(a => !a.Correct && a.Leak);
        DecisionGrading.Grade(q[1], false, ["C3"], "", located).Should().Match<DecisionAnswer>(a => a.Correct && !a.Leak,
            "on its date the opt-in is the right answer");
        DecisionGrading.Grade(q[2], false, ["C1", "C3"], "", located).Should().Match<DecisionAnswer>(a => a.Correct && !a.Leak,
            "naming the old decision while answering what replaced it is not a leak");
        DecisionGrading.Grade(q[0], true, [], "", located).Should().Match<DecisionAnswer>(a => !a.Correct && a.Abstained);
        DecisionGrading.Grade(q.Last(), true, [], "", located).Correct.Should().BeTrue();
    }

    [Fact]
    public void The_metrics_are_the_pre_registered_ones()
    {
        var located = DecisionLabels.Locate(Fixture(), DecisionCorpus.FromChangelog(Changelog, "CHANGELOG.md", "C"));
        var q = DecisionLabels.Questions(Fixture());
        var answers = new[]
        {
            DecisionGrading.Grade(q[0], false, ["C3"], "", located),
            DecisionGrading.Grade(q[1], false, ["C3"], "", located),
            DecisionGrading.Grade(q[2], false, ["C1"], "", located),
            DecisionGrading.Grade(q[3], false, [], "", located),
        };

        var m = DecisionGrading.Summarize(answers);

        m["in_force_precision"].Should().Be(50);
        m["superseded_leak"].Should().Be(50);
        m["as_of_accuracy"].Should().Be(100);
        m["lineage_match"].Should().Be(100);
        m["abstention"].Should().Be(0);
    }

    [Fact]
    public void An_answer_may_only_cite_what_it_was_shown_and_unreadable_JSON_abstains()
    {
        var shown = new HashSet<string> { "A1", "A2" };

        DecisionAnswerer.Parse("""{"abstain": false, "answer": "x", "decision_sources": ["A2", "A9", "[A1]"]}""", shown)
            .Cited.Should().Equal("A2", "A1");
        DecisionAnswerer.Parse("not json", shown).Abstained.Should().BeTrue();
    }
}
