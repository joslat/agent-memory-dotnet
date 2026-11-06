using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Intent;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The product intent pack (CMP-6): requirements read in EARS form with their ids and statuses, Gherkin scenarios that verify
/// them by tag, OpenSLO objectives and ideas, into one snapshot the manifest accepts; a scenario tagged with an id counts as
/// covering the requirement, never as a run that passed.
/// </summary>
public sealed class IntentPackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("intent-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    private void Repository()
    {
        Write("docs/requirements/orders.md", """
            # Orders

            - REQ-1: The system shall keep every order for seven years. (accepted)
            - REQ-2: When a customer places an order, the system shall send a confirmation within one minute.
            - **REQ-3** While the payment provider is down, the system shall queue payments.
            - [REQ-4] If a payment fails twice, then the system shall cancel the order. [implemented]
            - Where gift wrapping is offered, the system shall show its price.
            Some prose that never says it must.
            """);
        Write("REQUIREMENTS.md", "- REQ-5: While stock is low, when an order arrives, the system shall warn the buyer.\n");
        Write("features/ordering.feature", """
            @orders
            Feature: Ordering
              Customers place orders and get a confirmation.

              Background:
                Given a signed-in customer

              @REQ-2
              Scenario: A confirmation follows the order
                Given an empty basket
                When the customer orders one book
                Then a confirmation arrives

              Rule: Payments
                @req:REQ-4 @slow
                Scenario Outline: A payment that fails twice cancels the order
                  Given a payment that fails <times> times
                  Then the order is <state>
                  Examples:
                    | times | state     |
                    | 2     | cancelled |
            """);
        Write("features/nl.feature", "# language: nl\nFunctionaliteit: Bestellen\n");
        Write("ops/checkout.slo.yaml", """
            apiVersion: openslo/v1
            kind: SLO
            metadata:
              name: checkout-availability
              displayName: Checkout availability
            spec:
              service: checkout
              description: Checkout answers.
              indicatorRef: checkout-success-ratio
              timeWindow:
                - duration: 28d
                  isRolling: true
              objectives:
                - target: 0.995
            """);
        Write("IDEAS.md", """
            # Ideas

            ## One-click reorder
            Let a customer repeat an order.

            ## Gift cards
            Status: parked
            Sell gift cards at checkout.
            """);
    }

    private ModuleSnapshot Read(out IReadOnlyList<string> skipped)
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'));
        return IntentReader.Read(_root, IntentReader.IntentFiles(files), "scan", out skipped);
    }

    private static ModuleProposal Record(ModuleSnapshot snapshot, string type, string key) => snapshot.Records.Single(r => r.Type == type && r.Key == key);

    private static string Text(ModuleProposal record, string property) => record.Properties[property].ToString();

    [Fact]
    public void A_repository_reads_into_one_snapshot_that_the_manifest_accepts()
    {
        Repository();

        var snapshot = Read(out var skipped);

        snapshot.Problems().Should().BeEmpty();
        skipped.Should().ContainSingle().Which.Should().Contain("nl.feature").And.Contain("only English");

        var requirements = snapshot.Records.Where(r => r.Type == IntentPack.RequirementType).ToDictionary(r => Text(r, "reference"), r => r);
        requirements.Keys.Should().Contain(["REQ-1", "REQ-2", "REQ-3", "REQ-4", "REQ-5"]);
        Text(requirements["REQ-1"], "pattern").Should().Be("ubiquitous");
        Text(requirements["REQ-1"], "status").Should().Be("accepted");
        Text(requirements["REQ-1"], "text").Should().NotContain("(accepted)");
        Text(requirements["REQ-2"], "pattern").Should().Be("event");
        Text(requirements["REQ-3"], "pattern").Should().Be("state");
        Text(requirements["REQ-4"], "pattern").Should().Be("unwanted");
        Text(requirements["REQ-4"], "status").Should().Be("implemented");
        Text(requirements["REQ-5"], "pattern").Should().Be("complex");
        requirements.Values.Should().Contain(r => Text(r, "pattern") == "optional" && Text(r, "reference").StartsWith("docs/requirements/orders.md:", StringComparison.Ordinal));
        Text(requirements["REQ-2"], "status").Should().Be("proposed", "a tagged scenario covers it; it does not make it verified");
        Text(requirements["REQ-2"], "scenarios").Should().Be("1");

        var confirmation = snapshot.Records.Single(r => r.Type == IntentPack.ScenarioType && Text(r, "name") == "A confirmation follows the order");
        Text(confirmation, "steps").Should().Be("Given an empty basket\nWhen the customer orders one book\nThen a confirmation arrives");
        confirmation.Links.Select(l => (l.Relation, l.Target)).Should().BeEquivalentTo([
            (IntentPack.ScenarioOf, "features/ordering.feature"), (IntentPack.Verifies, requirements["REQ-2"].Key)]);
        var outline = snapshot.Records.Single(r => r.Type == IntentPack.ScenarioType && Text(r, "name").StartsWith("A payment", StringComparison.Ordinal));
        Text(outline, "outline").Should().Be("yes");
        Text(outline, "rule").Should().Be("Payments");
        Text(outline, "tags").Should().Contain("@orders").And.Contain("@slow");
        outline.Links.Should().Contain(l => l.Relation == IntentPack.Verifies && l.Target == requirements["REQ-4"].Key);
        Text(Record(snapshot, IntentPack.FeatureType, "features/ordering.feature"), "description").Should().Be("Customers place orders and get a confirmation.");

        var objective = Record(snapshot, IntentPack.ObjectiveType, "ops/checkout.slo.yaml#checkout-availability");
        Text(objective, "name").Should().Be("Checkout availability");
        Text(objective, "target").Should().Be("0.995");
        Text(objective, "window").Should().Be("28d");
        Text(objective, "service").Should().Be("checkout");

        var ideas = snapshot.Records.Where(r => r.Type == IntentPack.IdeaType).ToList();
        ideas.Select(i => (Text(i, "title"), Text(i, "status"))).Should().BeEquivalentTo([("One-click reorder", "open"), ("Gift cards", "parked")]);
    }

    [Fact]
    public void Candidate_files_are_found_by_name()
    {
        IntentReader.IntentFiles(["REQUIREMENTS.md", "docs/requirements/a.md", "x.req.md", "README.md", "a.feature", "ops/api.slo.yaml", "openslo/x.yml", "IDEAS.md", "docs/ideas/b.md", "src/a.cs"])
            .Should().Equal("IDEAS.md", "REQUIREMENTS.md", "a.feature", "docs/ideas/b.md", "docs/requirements/a.md", "openslo/x.yml", "ops/api.slo.yaml", "x.req.md");
    }

    [Fact]
    public void Reading_twice_gives_the_same_snapshot()
    {
        Repository();
        var first = Read(out _);
        var second = Read(out _);
        second.Records.Select(r => (r.Type, r.Key, string.Join("|", r.Properties.Select(p => p.Key + "=" + p.Value)))).Should()
            .Equal(first.Records.Select(r => (r.Type, r.Key, string.Join("|", r.Properties.Select(p => p.Key + "=" + p.Value)))));
    }
}
