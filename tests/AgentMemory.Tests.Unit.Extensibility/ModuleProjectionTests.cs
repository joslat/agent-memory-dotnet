using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Decisions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// CMP-7: files as projections of the graph. The same records give the same bytes; a projection the records outgrew is
/// stale and rewritten; one edited by hand is never overwritten silently, and the check names the records it touched; the
/// AGENTS.md section is replaced in place, the rest of the file left as it was.
/// </summary>
public sealed class ModuleProjectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("projection-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ModuleProposal Decision(string key, string title, string status) => new()
    {
        Type = DecisionsPack.Type, Key = key,
        Properties = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement(title), ["status"] = JsonSerializer.SerializeToElement(status),
            ["decision"] = JsonSerializer.SerializeToElement("We do " + title.ToLowerInvariant() + "."), ["source"] = JsonSerializer.SerializeToElement("docs/adr/" + key + ".md"),
        },
        Source = new ProposalSource { Excerpt = key },
    };

    private static ModuleSnapshot Snapshot(params ModuleProposal[] records) =>
        new() { Module = DecisionsPack.Manifest, Source = "git:abc1234", Records = records };

    [Fact]
    public void The_same_records_give_the_same_bytes_in_key_order()
    {
        var a = ModuleProjection.Render(Snapshot(Decision("0002-b", "Use Neo4j", "accepted"), Decision("0001-a", "Keep history", "accepted")));
        var b = ModuleProjection.Render(Snapshot(Decision("0001-a", "Keep history", "accepted"), Decision("0002-b", "Use Neo4j", "accepted")));

        a.Should().Be(b);
        a.Path.Should().Be("docs/memory/decisions.md");
        a.Content.Should().StartWith("<!-- agentmemory:projection module=io.agentmemory.packs.decisions ");
        a.Content.IndexOf("### Keep history", StringComparison.Ordinal).Should().BeLessThan(a.Content.IndexOf("### Use Neo4j", StringComparison.Ordinal));
        a.Content.Should().Contain("Decision (accepted, ): Keep history. We do keep history.");
        ModuleProjection.Edited(a.Content).Should().BeFalse();
    }

    [Fact]
    public void A_new_commit_alone_does_not_make_a_projection_stale()
    {
        var at = ModuleProjection.Render(Snapshot(Decision("0001-a", "Keep history", "accepted")));
        var later = ModuleProjection.Render(Snapshot(Decision("0001-a", "Keep history", "accepted")) with { Source = "git:def5678" });

        later.Should().Be(at, "a projection that changed with every commit, its own included, could never be up to date");
    }

    [Fact]
    public void A_stale_projection_is_rewritten_and_a_hand_edit_is_never_overwritten_silently()
    {
        var first = ModuleProjection.Render(Snapshot(Decision("0001-a", "Keep history", "accepted"), Decision("0002-b", "Use Neo4j", "proposed")));
        ModuleProjection.Check(_root, [first]).Single().State.Should().Be(ProjectionState.Missing);
        ModuleProjection.Write(_root, [first]);
        ModuleProjection.Check(_root, [first]).Single().State.Should().Be(ProjectionState.UpToDate);

        // The sources moved on: the file on disk is untouched, so it is stale, and the changed record is named.
        var second = ModuleProjection.Render(Snapshot(Decision("0001-a", "Keep history", "accepted"), Decision("0002-b", "Use Neo4j", "accepted")));
        var stale = ModuleProjection.Check(_root, [second]).Single();
        stale.State.Should().Be(ProjectionState.Stale);
        stale.Records.Should().Equal("0002-b");
        ModuleProjection.Write(_root, [second]);
        ModuleProjection.Check(_root, [second]).Single().State.Should().Be(ProjectionState.UpToDate);

        // Someone edits the projection instead of the source: flagged, named, and kept.
        var path = Path.Combine(_root, second.Path);
        var edited = File.ReadAllText(path).Replace("We do keep history.", "We keep history for ten years.", StringComparison.Ordinal);
        File.WriteAllText(path, edited);
        var check = ModuleProjection.Check(_root, [second]).Single();
        check.State.Should().Be(ProjectionState.Edited);
        check.Records.Should().Equal("0001-a");
        ModuleProjection.Write(_root, [second]).Single().State.Should().Be(ProjectionState.Edited);
        File.ReadAllText(path).Should().Be(edited, "a projection never overwrites a person's edit silently");
        ModuleProjection.Write(_root, [second], force: true);
        File.ReadAllText(path).Should().Be(second.Content);
    }

    [Fact]
    public void The_AGENTS_section_is_replaced_in_place_and_a_hand_edited_one_is_kept()
    {
        var snapshot = Snapshot(Decision("0001-a", "Keep history", "accepted"));
        var projection = ModuleProjection.Render(snapshot);
        const string mine = "# Agents\n\nRun the tests before you push.\n";

        var once = ModuleProjection.AgentsSection(mine, snapshot, projection, force: false, out var edited);
        edited.Should().BeFalse();
        once.Should().StartWith(mine.TrimEnd('\n')).And.Contain("<!-- agentmemory:begin io.agentmemory.packs.decisions ").And.Contain("Held: 1 decision;");
        ModuleProjection.AgentsSection(once, snapshot, projection, force: false, out _).Should().Be(once, "the same records give the same section");

        var more = Snapshot(Decision("0001-a", "Keep history", "accepted"), Decision("0002-b", "Use Neo4j", "accepted"));
        var twice = ModuleProjection.AgentsSection(once, more, ModuleProjection.Render(more), force: false, out edited);
        edited.Should().BeFalse();
        twice.Should().Contain("Held: 2 decisions;").And.StartWith(mine.TrimEnd('\n'));

        var handEdited = twice.Replace("Held: 2 decisions;", "Held: many decisions;", StringComparison.Ordinal);
        ModuleProjection.AgentsSection(handEdited, more, ModuleProjection.Render(more), force: false, out edited).Should().Be(handEdited);
        edited.Should().BeTrue();
        ModuleProjection.AgentsSection(handEdited, more, ModuleProjection.Render(more), force: true, out _).Should().Be(twice);
    }
}
