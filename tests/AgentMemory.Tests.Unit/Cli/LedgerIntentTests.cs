#pragma warning disable AMEXT001
using AgentMemory.Cli.Commands;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Packs.Intent;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

/// <summary>
/// CO-2d: this repository's task ledger read as product intent (the owner's test bed choice (b)). Each row is a requirement whose
/// statement is its task and the owner's words about it; its progress is the work layer's, so a status flip or a progress note
/// leaves the requirement as it was, and only a requirement- or decision-level change writes a new version of it.
/// </summary>
public sealed class LedgerIntentTests
{
    private const string Ledger = """
        {"tasks":[
          {"id":"CO-2","task":"Design the coding ontology","status":"Done","notes":"Owner, 22 Nov: 'layers, please'. [2026-11-22] Design written. [2026-11-22] Owner (docket): all three as recommended. [2026-11-23] Ownership of names is unchanged."},
          {"id":"CO-2a","task":"Composite layers","status":"Done","notes":"[2026-11-22] Built."},
          {"id":"CO-2b","task":"Slice 1 relations","status":"Pending"},
          {"id":"CMP-2","task":"Code map","status":"Done"},
          {"id":"CMP-2ab","task":"A child of CMP-2 by its letters","status":"Pending"},
          {"id":"X-1","task":"","status":"Pending"},
          {"task":"A row without an id"}
        ]}
        """;

    private static Dictionary<string, ModuleProposal> Rows(string ledger) =>
        LedgerIntent.Read(ledger, "task-ledger.json", "ledger:test").Records.ToDictionary(r => r.Key);

    private static string Shape(ModuleProposal r) =>
        string.Join("|", r.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "|" + string.Join(",", r.Links.Select(l => l.Relation + ">" + l.Target));

    [Fact]
    public void Each_row_with_an_id_and_a_task_is_an_accepted_requirement_that_its_lettered_children_decompose()
    {
        var snapshot = LedgerIntent.Read(Ledger, "task-ledger.json", "ledger:test");

        snapshot.Problems().Should().BeEmpty();
        snapshot.Module.Id.Should().Be(IntentPack.Id);
        var rows = snapshot.Records.ToDictionary(r => r.Key);
        rows.Keys.Should().BeEquivalentTo(["CO-2", "CO-2a", "CO-2b", "CMP-2", "CMP-2ab"]);
        rows.Values.Should().OnlyContain(r => r.Type == IntentPack.RequirementType && r.Properties["status"].GetString() == "accepted",
            "a row's progress is its work item's status, not the requirement's");
        rows["CO-2"].Properties["reference"].GetString().Should().Be("CO-2");
        rows["CO-2"].Properties["file"].GetString().Should().Be("task-ledger.json");
        rows["CO-2"].Properties["source"].GetString().Should().Be("task-ledger.json#CO-2");
        rows["CO-2"].Properties["text"].GetString().Should().Be(
            "Design the coding ontology\n\nOwner, 22 Nov: 'layers, please'.\n[2026-11-22] Owner (docket): all three as recommended.",
            "the owner's words about a row are part of what it asks; other notes, and 'Ownership', are not");
        rows["CO-2b"].Properties["text"].GetString().Should().Be("Slice 1 relations");
        rows["CO-2a"].Links.Select(l => (l.Relation, l.Target)).Should().Equal((IntentPack.Decomposes, "CO-2"));
        rows["CO-2b"].Links.Select(l => (l.Relation, l.Target)).Should().Equal((IntentPack.Decomposes, "CO-2"));
        rows["CMP-2ab"].Links.Select(l => (l.Relation, l.Target)).Should().Equal((IntentPack.Decomposes, "CMP-2"));
        rows["CO-2"].Links.Should().BeEmpty();
    }

    [Fact]
    public void A_status_flip_or_a_progress_note_leaves_every_requirement_as_it_was_and_a_new_task_text_or_owner_word_changes_only_its_row()
    {
        var before = Rows(Ledger);

        var progressed = Rows(Ledger.Replace("\"Slice 1 relations\",\"status\":\"Pending\"", "\"Slice 1 relations\",\"status\":\"Done\",\"notes\":\"[2026-11-23] Built and tested.\"", StringComparison.Ordinal)
            .Replace("[2026-11-22] Built.", "[2026-11-22] Built. [2026-11-23] Mutants re-run.", StringComparison.Ordinal));
        progressed.Keys.Should().BeEquivalentTo(before.Keys);
        progressed.Should().OnlyContain(r => Shape(r.Value) == Shape(before[r.Key]));

        var asked = Rows(Ledger.Replace("\"Composite layers\"", "\"Composite layers, with the walk's label\"", StringComparison.Ordinal)
            .Replace("[2026-11-22] Built.", "[2026-11-22] Built. [2026-11-23] Owner: keep the label out of the reason.", StringComparison.Ordinal)
            .Replace("\"Code map\",\"status\":\"Done\"", "\"Code map\",\"status\":\"Done\",\"notes\":\"[2026-11-23] Owner decision: tests first.\"", StringComparison.Ordinal));
        asked.Where(r => Shape(r.Value) != Shape(before[r.Key])).Select(r => r.Key).Should().BeEquivalentTo(["CO-2a", "CMP-2"]);
        asked["CO-2a"].Properties["text"].GetString().Should().Be("Composite layers, with the walk's label\n\n[2026-11-23] Owner: keep the label out of the reason.");
    }

    [Fact]
    public void A_long_owner_history_keeps_the_newest_words_so_a_new_one_always_changes_the_requirement()
    {
        var notes = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"[2026-10-{(i % 28) + 1:00}] Owner: point {i} {new string('x', 40)}."));
        var ledger = "{\"tasks\":[{\"id\":\"A-1\",\"task\":\"Keep it\",\"status\":\"Done\",\"notes\":\"" + notes + "\"}]}";
        var longer = "{\"tasks\":[{\"id\":\"A-1\",\"task\":\"Keep it\",\"status\":\"Done\",\"notes\":\"" + notes + " [2026-11-23] Owner: point 61.\"}]}";

        var text = Rows(ledger)["A-1"].Properties["text"].GetString()!;
        var newer = Rows(longer)["A-1"].Properties["text"].GetString()!;

        text.Length.Should().BeLessThanOrEqualTo(2000);
        text.Should().StartWith("Keep it\n\n").And.Contain("point 60 ").And.NotContain("point 1 ");
        newer.Should().NotBe(text).And.EndWith("Owner: point 61.");
    }
}
