using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.4. Compiling the per-owner block: determinism, the token budget, and the hash.
/// </summary>
/// <remarks>
/// The compile itself is pure and testable without a database — deliberately, because the two
/// properties that matter most (byte-stability and the trimming order) are properties of the text, not
/// of the graph.
/// </remarks>
public sealed class WorkingMemoryTests
{
    private static readonly string[] Facts =
        ["user name Alice", "user works_at Acme", "user lives_in Zurich"];

    private static readonly string[] Preferences =
        ["[food] prefers vegetarian", "[travel] window seat"];

    private static readonly string[] Entities = ["Acme (ORGANIZATION)", "Zurich (LOCATION)"];

    [Fact]
    public void TheSameInputsProduceByteIdenticalText()
    {
        // Byte-stability is what makes the hash short-circuit meaningful AND what keeps prompt-prefix
        // caching working. A block that reshuffled equal-ranked rows would rewrite on every burst.
        var first = Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, 300);
        var second = Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, 300);

        second.Should().Be(first);
        Neo4jWorkingMemoryService.Hash(second).Should().Be(Neo4jWorkingMemoryService.Hash(first));
    }

    [Fact]
    public void AllThreeSectionsAreTitledAndOrdered()
    {
        var text = Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, 300);

        text.Should().Contain("Stable facts:").And.Contain("Active preferences:").And.Contain("Key entities:");
        text.IndexOf("Stable facts:", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Active preferences:", StringComparison.Ordinal));
        text.IndexOf("Active preferences:", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Key entities:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptySectionIsOmittedRatherThanRenderedEmpty()
    {
        // An empty heading tells the model "this was consulted and is empty", which is a different and
        // wrong claim -- the same defect the formatter's bare-heading guard exists for.
        var text = Neo4jWorkingMemoryService.Compose(Facts, [], [], 300);

        text.Should().Contain("Stable facts:")
            .And.NotContain("Active preferences:").And.NotContain("Key entities:");
    }

    [Fact]
    public void NothingAtAllRendersAnEmptyBlock()
    {
        Neo4jWorkingMemoryService.Compose([], [], [], 300).Should().BeEmpty();
    }

    [Fact]
    public void TheBudgetDropsEntitiesFirstThenPreferencesThenFacts()
    {
        // Facts are the head of the question distribution -- name, job, stable attributes -- so they
        // are the LAST thing sacrificed to the budget. Getting this order backwards would spend the
        // block's tokens on its least useful section.
        var tiny = Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, maxTokens: 20);

        Neo4jWorkingMemoryService.EstimateTokens(tiny).Should().BeLessThanOrEqualTo(20);
        tiny.Should().Contain("Stable facts:");
        tiny.Should().NotContain("Key entities:", "entities go first when the budget binds");
    }

    [Fact]
    public void AGenerousBudgetKeepsEverything()
    {
        var text = Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, 300);

        foreach (var line in Facts.Concat(Preferences).Concat(Entities))
            text.Should().Contain(line);
    }

    [Fact]
    public void AnImpossibleBudgetYieldsAnEmptyBlockRatherThanLooping()
    {
        // The trimming loop must terminate even when a single line cannot fit.
        Neo4jWorkingMemoryService.Compose(Facts, Preferences, Entities, maxTokens: 1)
            .Should().BeEmpty();
    }

    [Fact]
    public void DifferentContentProducesADifferentHash()
    {
        var a = Neo4jWorkingMemoryService.Compose(Facts, [], [], 300);
        var b = Neo4jWorkingMemoryService.Compose(Facts.Take(2).ToList(), [], [], 300);

        Neo4jWorkingMemoryService.Hash(a).Should().NotBe(Neo4jWorkingMemoryService.Hash(b));
    }

    [Fact]
    public void TheTokenEstimatorIsTheOneTheBudgetIsExpressedIn()
    {
        // ceil(chars / 4). Pinned because the budget number in the options doc means nothing without it.
        Neo4jWorkingMemoryService.EstimateTokens("").Should().Be(0);
        Neo4jWorkingMemoryService.EstimateTokens("abcd").Should().Be(1);
        Neo4jWorkingMemoryService.EstimateTokens("abcde").Should().Be(2);
    }

    [Fact]
    public void TheTierIsOffByDefault()
    {
        var options = new WorkingMemoryOptions();

        options.Enabled.Should().BeFalse();
        options.RebuildOnWrite.Should().BeTrue("but only matters once Enabled is true");
        options.ClearOnRebuildFailure.Should().BeTrue(
            "absence degrades to today's behaviour; staleness manufactures knowledge-update errors");
    }

    [Fact]
    public void TheOptionsAreMutableSoAConfigureLambdaCanReachThem()
    {
        // The issue-#100 regression class: an init-only sub-option binds, validates, and silently keeps
        // its default -- code that compiles, runs, and configures nothing.
        var options = new MemoryOptions();

        options.WorkingMemory.Enabled = true;
        options.WorkingMemory.MaxTokens = 120;
        options.WorkingMemory.MaxStableFacts = 4;
        options.WorkingMemory.MinPreferenceConfidence = 0.8;

        options.WorkingMemory.Enabled.Should().BeTrue();
        options.WorkingMemory.MaxTokens.Should().Be(120);
        options.WorkingMemory.MaxStableFacts.Should().Be(4);
        options.WorkingMemory.MinPreferenceConfidence.Should().Be(0.8);
    }
}
