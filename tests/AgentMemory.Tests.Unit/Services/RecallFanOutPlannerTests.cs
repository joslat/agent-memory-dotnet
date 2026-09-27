using System.Diagnostics;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The fan-out gate's named rules, positive and negative (30.10, design step 4).
/// </summary>
/// <remarks>
/// <para>
/// Every rule gets a negative fixture as well as a positive one. A gate tested only on what should
/// fire is a gate that fires on everything and still passes, and the routing analysis this design
/// draws on found exactly that failure in two earlier heuristics.
/// </para>
/// <para>
/// The named examples are the routing document's own: the conjunction case fires; a bare greeting, a
/// single temporal ask, and a paper title ending in a question mark do not.
/// </para>
/// </remarks>
public sealed class RecallFanOutPlannerTests
{
    private static RecallFanOutOptions Options() => new();

    private static (bool Fired, string[] Rules) Gate(string query) =>
        RecallFanOutPlanner.EvaluateGate(query, Options());

    // ── C1: multi-wh ────────────────────────────────────────────────────

    [Theory]
    [InlineData("My brother Pablo lives in Seville and my sister Lena lives in Berlin.")]
    [InlineData("I met Dana and Tomás at Northwind in March 2026")]
    public void H3_AStatementIsNeverSplit(string statement)
    {
        // Measured: the first one fired C2 and cost the answer path an extra embedding and two more vector
        // searches per memory type. A statement asks nothing; there is nothing to fan out.
        var (fired, _) = Gate(statement);

        fired.Should().BeFalse();
    }

    [Theory]
    [InlineData("Tell me about Pablo and Lena")]
    [InlineData("Compare Acme Corp, Initech and Globex")]
    [InlineData("where do Pablo and Lena live")]
    [InlineData("Does Pablo live near Lena and Dana")]
    // The review's cases: requests without a question mark, and a keyword query.
    [InlineData("Please tell me about Pablo and Lena")]
    [InlineData("I'd like to know Pablo's and Lena's cities")]
    [InlineData("I want to know about Pablo and Lena")]
    [InlineData("Pablo and Lena birthdays")]
    // Review round 2: a contracted interrogative, and an imperative ending in a full stop.
    [InlineData("What's the city Pablo and Lena live in")]
    [InlineData("Suggest a gift for Pablo and Lena.")]
    public void H3_ARequestOrAQuestionWithoutItsMarkStillFansOut(string ask)
    {
        var (fired, _) = Gate(ask);

        fired.Should().BeTrue();
    }

    [Fact]
    public void C1_FiresOnTwoDistinctInterrogatives()
    {
        var (fired, rules) = Gate("Where did I work and what did I study?");

        fired.Should().BeTrue();
        rules.Should().Contain("C1");
    }

    [Fact]
    public void C1_DoesNotFireOnOneInterrogativeRepeated()
    {
        // "what ... what" is one lemma twice, not two asks. Counting occurrences rather than distinct
        // lemmas would make any rambling single question look compound.
        var (_, rules) = Gate("what did i eat, what");

        rules.Should().NotContain("C1");
    }

    [Fact]
    public void TheGateNeverCountsQuestionMarks()
    {
        // Measured wrong on 3 of 5 cases. A paper title ending in "?" is not a question at all.
        var (fired, _) = Gate("is all you need attention?");

        fired.Should().BeFalse(
            "a title ending in a question mark is not a compound query, and mark-counting says it is");
    }

    // ── C2: conjunction with entities on both sides ─────────────────────

    [Fact]
    public void C2_FiresWhenEntitiesFlankAJoiner()
    {
        var (fired, rules) = Gate("Tell me about the MoMA visit and the Met dinner");

        fired.Should().BeTrue();
        rules.Should().Contain("C2");
    }

    [Fact]
    public void C2_DoesNotFireOnAJoinerWithoutEntities()
    {
        // Ordinary prose is full of "and". Without a named thing on each side there is nothing to
        // split the query along.
        var (_, rules) = Gate("i went out and came back");

        rules.Should().NotContain("C2");
    }

    // ── E3: distinct entity mentions ────────────────────────────────────

    [Fact]
    public void E3_FiresAtTheConfiguredNumberOfDistinctEntities()
    {
        var (fired, rules) = Gate("Compare Acme Corp, Initech and Globex");

        fired.Should().BeTrue();
        rules.Should().Contain("E3");
    }

    [Fact]
    public void E3_IgnoresCapitalisedStopwords()
    {
        // "What", "The" and "I" are capitalised by grammar, not because they name anything. Counting
        // them would let any well-formed sentence satisfy an entity rule.
        var (_, rules) = Gate("What did The report say? I forget.");

        rules.Should().NotContain("E3");
    }

    [Fact]
    public void E3_RespectsItsConfiguredThreshold()
    {
        var strict = new RecallFanOutOptions { MinDistinctEntityMentions = 5 };

        RecallFanOutPlanner.EvaluateGate("Compare Acme Corp, Initech and Globex", strict)
            .Rules.Should().NotContain("E3", "three mentions must not satisfy a threshold of five");
    }

    // ── D4: date mixed with a non-temporal ask ──────────────────────────

    [Fact]
    public void D4_FiresOnADateWithANonTemporalAsk()
    {
        var (fired, rules) = Gate("Where was I working in March 2026?");

        fired.Should().BeTrue();
        rules.Should().Contain("D4");
    }

    [Fact]
    public void D4_DoesNotFireOnAPurelyTemporalAsk()
    {
        // "When did X happen" is one temporal question. There is no second memory type to fan out to.
        var (_, rules) = Gate("when did that happen last week");

        rules.Should().NotContain("D4");
    }

    [Fact]
    public void D4_IsNeverKeyedOnTheWordPrevious()
    {
        // Measured as an 82.1% false-positive surface: "my previous answer" is about the conversation,
        // not about time.
        var (fired, _) = Gate("what did my previous answer say");

        fired.Should().BeFalse();
    }

    // ── negatives that must never fire ──────────────────────────────────

    [Theory]
    [InlineData("hi")]
    [InlineData("thanks!")]
    [InlineData("ok got it")]
    public void ABareGreetingNeverFires(string greeting)
    {
        Gate(greeting).Fired.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputNeverFiresAndNeverThrows(string? query)
    {
        var (fired, rules) = Gate(query!);

        fired.Should().BeFalse();
        rules.Should().BeEmpty();
    }

    // ── the catastrophic-input guard ────────────────────────────────────

    [Fact]
    public void APathologicalInputIsScannedInLinearTime()
    {
        // The reason this gate is a token scan and not a regex: a regex needs a timeout, a timed-out
        // regex is a second source of nondeterminism, and a gate that decides differently on a slow
        // machine voids every run measured through it. 30 repeated fragments must not be a problem.
        var pathological = string.Join(" and ", Enumerable.Repeat(
            "What about the Acme Corp report from March 2026", 30));

        var watch = Stopwatch.StartNew();
        var (fired, _) = Gate(pathological);
        watch.Stop();

        fired.Should().BeTrue("this input is genuinely compound");
        watch.ElapsedMilliseconds.Should().BeLessThan(50,
            "a token scan is linear; a backtracking regex here would not be");
    }

    [Fact]
    public void TheRuleSetIsOrOnly_SoOneSignalIsEnough()
    {
        // OR-only is deliberate: a false fire is raise-only (one derivation, a few embeddings) and can
        // never remove a row from recall, so the gate is tuned to admit rather than to be precise.
        var (fired, rules) = Gate("Where was I working in March 2026?");

        fired.Should().BeTrue();
        rules.Should().HaveCountGreaterThanOrEqualTo(1);
    }
}
