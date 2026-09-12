using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The corpus's reachable ceiling, read from the meta resource AgentEval ships and we never opened.
/// </summary>
/// <remarks>
/// <para>
/// A question needing more gold sessions than <c>k_ref</c> cannot be fully answered from
/// <c>k_ref</c>, so the corpus declares a per-<c>g</c> ceiling. Conjunction caps 13 of 65 questions
/// at <c>k_ref=5</c>, for a corpus ceiling of ~0.947 — meaning **a perfect engine cannot score
/// 100%**, and every score this project reported against an implicit 1.0 was read against the wrong
/// scale.
/// </para>
/// <para>
/// These assert against the SHIPPED package rather than a fixture, deliberately: the point of the
/// type is that the numbers travel with the corpus the run loads. A fixture would pass while the
/// real resource moved.
/// </para>
/// </remarks>
public sealed class ReachableCeilingTests
{
    [Fact]
    public void ConjunctionDeclaresACeilingBelowOne()
    {
        var ceiling = TypedMemEvalReachableCeiling.For("conjunction");

        ceiling.Should().NotBeNull("the shipped corpus declares ceiling.by_g and g_distribution");
        ceiling!.Value.Questions.Should().Be(65);
        ceiling.Value.CappedQuestions.Should().BeGreaterThan(0,
            "questions needing more gold sessions than k_ref cannot reach 1.0");
        ceiling.Value.Fraction.Should().BeLessThan(1.0)
            .And.BeGreaterThan(0.8, "a sanity band — a ceiling near zero would mean a parse error");
    }

    [Fact]
    public void TheKReferenceBudgetIsReported() =>
        TypedMemEvalReachableCeiling.For("conjunction")!.Value.KRef.Should().BePositive(
            "the ceiling is only meaningful alongside the budget it was computed at");

    /// <summary>A score against reachable must exceed the same score against 1.0.</summary>
    [Fact]
    public void ShareOfReachableIsLargerThanShareOfAll()
    {
        var ceiling = TypedMemEvalReachableCeiling.For("conjunction")!.Value;

        var ofReachable = ceiling.ShareOfReachable(12);
        var ofAll = 12.0 / ceiling.Questions;

        ofReachable.Should().BeGreaterThan(ofAll,
            "the denominator shrinks when structurally unanswerable questions are excluded, so "
            + "reporting against 1.0 understates the engine and overstates the remaining gap");
    }

    /// <summary>An unknown vertical is null, never a confident 1.0.</summary>
    /// <remarks>
    /// Defaulting a missing ceiling to 1.0 would silently restore the assumption this type removes —
    /// the same null-versus-zero failure this repository has now hit in four separate instruments.
    /// </remarks>
    [Fact]
    public void AnUnknownVerticalIsNotSilentlyPerfect() =>
        TypedMemEvalReachableCeiling.For("not-a-vertical").Should().BeNull();

    /// <summary>
    /// EVERY vertical the loaded package ships must declare a ceiling.
    /// </summary>
    /// <remarks>
    /// Deliberately enumerated from the package rather than hardcoded. A fixed list failed the
    /// moment it named `procedural`, which exists only from 0.34 while the pin still read 0.33 — the
    /// test was right and the PIN was the defect, but a hardcoded list turns a pin question into a
    /// red suite. Enumerating makes the invariant pin-independent and strictly stronger: it covers
    /// whatever the package actually contains, including verticals added later.
    /// </remarks>
    [Fact]
    public void EveryShippedVerticalDeclaresOne()
    {
        var missing = TypedMemEvalVerticals.All
            .Select(descriptor => descriptor.Slug)
            .Where(slug => TypedMemEvalReachableCeiling.For(slug) is null)
            .ToArray();

        missing.Should().BeEmpty(
            "the C-D scoreboard ranks by score-versus-reachable, so a shipped vertical without a "
            + "ceiling cannot be placed on it");
    }
}
