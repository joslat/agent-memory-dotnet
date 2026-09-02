using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The gate standing between a re-grade and a meaningless agreement rate.
/// </summary>
/// <remarks>
/// <para>
/// AgentEval redraws corpora keeping the <c>question_id</c> set 100% identical with zero
/// byte-identical items, and neither <c>corpus_id</c> nor <c>revision</c> moves —
/// <c>corpus_sha256</c> is the only distinguishing field. For bitemporal, <b>27 of 60 items keep the
/// same question text and carry a different gold</b>.
/// </para>
/// <para>
/// The re-grade's replay is position-keyed and verifies question text per position, so it catches a
/// redraw that reworded anything and catches <b>nothing</b> when the text is stable and only the gold
/// moved. This comparison is what closes that, and it runs before any judge call — the cost of
/// getting it wrong is a paid pass whose output means nothing.
/// </para>
/// </remarks>
public class CorpusIdentityTests
{
    private const string Bitemporal = "f5b384d7f0ff9c0fbef8b962a5f4d678";
    private const string Redrawn = "abf2f3f4f0ff9c0fbef8b962a5f4d678";

    [Fact]
    public void TheSameCorpusMatches() =>
        TypedMemEvalRegradeProgram.CorpusIdentity.Verify(Bitemporal, Bitemporal)
            .Should().Be(TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Match);

    [Fact]
    public void CaseDoesNotMakeTwoShasDifferent() =>
        TypedMemEvalRegradeProgram.CorpusIdentity.Verify(Bitemporal, Bitemporal.ToUpperInvariant())
            .Should().Be(TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Match,
                "artifacts and manifests disagree about hex casing and that is not a redraw");

    [Fact]
    public void ARedrawnCorpusIsAMismatch() =>
        TypedMemEvalRegradeProgram.CorpusIdentity.Verify(Bitemporal, Redrawn)
            .Should().Be(TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Mismatch);

    /// <summary>
    /// The outcome that must stay its own thing: absence of evidence is not evidence of a match.
    /// </summary>
    /// <remarks>
    /// Folded into Match it passes silently — which is the constant-column failure this project has
    /// been bitten by three times. Folded into Mismatch it blocks every artifact older than
    /// provenance capture, which would make the gate so obstructive it gets bypassed. Hence three
    /// verdicts, and hence a warning rather than a refusal at the call site.
    /// </remarks>
    [Theory]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    [InlineData(null, null)]
    [InlineData("", "abc")]
    [InlineData("   ", "abc")]
    public void AnUnknownShaIsUnverifiableAndNotAMatch(string? stored, string? current) =>
        TypedMemEvalRegradeProgram.CorpusIdentity.Verify(stored, current)
            .Should().Be(TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Unverifiable);

    /// <summary>A mismatch on a malformed sha must still be a MISMATCH, not an exception.</summary>
    /// <remarks>
    /// The abort path formats both shas into its message. It previously sliced them at a fixed 16
    /// characters, which throws on a truncated or corrupt value — in the one branch whose entire job
    /// is to fail safely with an exit code. Second time on this gate: #213 hardened the sha's type
    /// and left its length assumed.
    /// </remarks>
    [Theory]
    [InlineData("abc", "def")]
    [InlineData("f5b384d7", "abf2f3f4")]
    public void AShortOrMalformedShaStillCompares(string stored, string current) =>
        TypedMemEvalRegradeProgram.CorpusIdentity.Verify(stored, current)
            .Should().Be(TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Mismatch);
}
