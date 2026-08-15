using System.Reflection;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.6 step 11. The answer-presence gate can now check a numeric answer — but only against derived
/// facts, and only when one actually carries the value.
/// </summary>
/// <remarks>
/// <para>
/// This gate is the feature's <b>free</b> instrument: no judge, no answer model, no cold build. It has
/// always reported a numeric gold answer as <i>uncheckable</i> rather than absent, because "17 fish
/// total" is the sum of counts held separately and the numeral was never written. That was honest while
/// nothing computed such values.
/// </para>
/// <para>
/// The risk in changing a gate to measure a feature is that the gate stops being able to fail. Two
/// properties keep it honest here: derived text is checked <b>separately</b>, so an aggregate can never
/// satisfy a non-numeric answer by coincidence; and a numeric question stays uncheckable unless a
/// derived fact actually carries the value, so the accountant's silence is never scored as the
/// feature's success.
/// </para>
/// </remarks>
public sealed class DerivedAnswerPresenceTests
{
    private static readonly Type Gate =
        typeof(AgentMemory.LongMemEval.LongMemEvalPreparationManifest).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalAnswerPresence")!;

    private static (bool Checkable, bool Present) Evaluate(
        string gold, string[] memory, string[]? derived = null)
    {
        var method = Gate.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "Evaluate" && m.GetParameters().Length == 3);
        var result = method.Invoke(
            null, [gold, (IReadOnlyCollection<string>)memory, (IReadOnlyCollection<string>)(derived ?? [])])!;

        return (
            (bool)result.GetType().GetProperty("Checkable")!.GetValue(result)!,
            (bool)result.GetType().GetProperty("Present")!.GetValue(result)!);
    }

    // ── the pre-30.6 behaviour, unchanged ─────────────────────────────

    [Fact]
    public void WithNoDerivedFactsANumericAnswerIsStillUncheckable()
    {
        // Byte-for-byte the old answer, so every archived checkable-rate stays comparable.
        Evaluate("750", ["the user had 800 dollars", "then 50 dollars"])
            .Should().Be((false, false));
    }

    [Fact]
    public void ANonNumericAnswerIsUnaffectedByDerivedFactsEntirely()
    {
        // The separation that keeps the floor a floor: an enumeration listing cities must not start
        // covering tokens the gate is supposed to find in EXTRACTED memory.
        var withoutDerived = Evaluate("Lisbon", ["the user visited Lisbon"]);
        var withDerived = Evaluate("Lisbon", ["the user visited Lisbon"], ["Lisbon; Paris; Rome"]);

        withDerived.Should().Be(withoutDerived);
    }

    [Fact]
    public void ADerivedFactCannotRescueANonNumericAnswerTheMemoryLacks()
    {
        // The inverse, and the one that would quietly inflate the metric: derived text is never
        // consulted for a lexical answer.
        Evaluate("Reykjavik", ["the user visited Lisbon"], ["Lisbon; Paris; Reykjavik"])
            .Should().Be((true, false));
    }

    // ── the new capability ────────────────────────────────────────────

    [Fact]
    public void ANumericAnswerBecomesCheckableWhenADerivedFactCarriesIt()
    {
        Evaluate("750", ["the user had 800 dollars", "then 50 dollars"], ["750"])
            .Should().Be((true, true));
    }

    [Fact]
    public void ADerivedFactCarryingADifferentNumberIsCheckableAndAbsent()
    {
        // The feature can be WRONG and the gate has to be able to say so, or it stops being a
        // measurement of anything.
        Evaluate("750", ["the user had 800 dollars"], ["700"])
            .Should().Be((false, false),
                "no token of the gold answer appears, so this stays uncheckable rather than becoming a "
                + "scored failure of extraction");
    }

    [Fact]
    public void DerivedFactsUnrelatedToTheQuestionLeaveItUncheckable()
    {
        // The accountant's silence on THIS question must not be scored as the feature working, nor as
        // extraction failing.
        Evaluate("750", ["the user had 800 dollars"], ["3", "Lisbon; Paris"])
            .Should().Be((false, false));
    }

    [Fact]
    public void TheRenderedDerivationDoesNotByItselfMakeAnAnswerPresent()
    {
        // A derivation string names its inputs -- "800 (a1) - 50 (b2)" -- so a gate that read the
        // derivation as evidence would mark "800" present purely because the arithmetic mentioned it.
        // Here the gold IS 750 and the derived text contains only the inputs.
        Evaluate("750", ["the user had 800 dollars"], ["800 (a1) - 50 (b2)"])
            .Should().Be((false, false));
    }

    [Fact]
    public void AMultiTokenNumericAnswerNeedsMostOfItsTokens()
    {
        // The same 0.5 coverage threshold the lexical path uses, applied unchanged -- a derived path
        // with a laxer bar would report a higher rate for reasons unrelated to the feature.
        Evaluate("12 5 17", ["irrelevant"], ["17"]).Should().Be((true, false));
        Evaluate("12 5 17", ["irrelevant"], ["12 5 17"]).Should().Be((true, true));
    }

    // ── the old overload still exists ─────────────────────────────────

    [Fact]
    public void TheTwoArgumentOverloadStillWorksAndMeansNoDerivedFacts()
    {
        // Every existing call site keeps compiling AND keeps meaning what it meant.
        var method = Gate.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "Evaluate" && m.GetParameters().Length == 2);

        var result = method.Invoke(
            null, ["750", (IReadOnlyCollection<string>)new[] { "800 dollars" }])!;

        result.GetType().GetProperty("Checkable")!.GetValue(result).Should().Be(false);
    }
}
