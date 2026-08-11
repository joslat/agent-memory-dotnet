using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Exact token counting for figures that leave the repository, and an honest label when it cannot.
/// </summary>
public sealed class TokenCounterTests
{
    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4")]
    [InlineData("gpt-35-turbo")]
    [InlineData("my-gpt-4o-deployment")]
    public void AKnownModelCountsExactly(string modelId)
    {
        var counter = new LongMemEvalTokenCounter(modelId);

        counter.Method.Should().Be(TokenCountMethod.Exact);
        counter.Count("The quick brown fox jumps over the lazy dog.").Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("llama-3")]
    [InlineData("some-unlabelled-deployment")]
    [InlineData(null)]
    public void AnUnknownModelFallsBackToTheHeuristicAndSaysSo(string? modelId)
    {
        // Failing soft matters: a missing vocabulary must not fail a benchmark run. Reporting the
        // method matters more: a heuristic silently labelled exact is worse than no number at all.
        var counter = new LongMemEvalTokenCounter(modelId);

        counter.Method.Should().Be(TokenCountMethod.CharacterHeuristic);
        counter.Encoding.Should().BeNull();
    }

    [Fact]
    public void AnUnknownModelNeverCountsWithTheWrongVocabulary()
    {
        // The failure mode this guards: quietly picking a default encoding for an unrecognised model
        // produces a confident, wrong number -- which is the shape of error that survives review.
        var unknown = new LongMemEvalTokenCounter("definitely-not-a-real-model");
        var text = "The quick brown fox jumps over the lazy dog.";

        unknown.Count(text).Should().Be(LongMemEvalContextSize.Estimate(text));
    }

    [Fact]
    public void TheHeuristicAndTheExactCountActuallyDisagree()
    {
        // The load-bearing test for why this exists at all. If 4-chars-per-token matched a real
        // tokenizer, none of this would be worth the dependency -- so assert they differ on ordinary
        // English prose, which is what a published figure would be computed over.
        const string prose =
            "Memory systems are often evaluated end to end, which conflates retrieval quality with "
            + "the answer model's competence. A token count is one of the few figures that cannot be "
            + "inflated by a better model, and is therefore worth measuring precisely.";

        var exact = new LongMemEvalTokenCounter("gpt-4o").Count(prose);
        var heuristic = LongMemEvalContextSize.Estimate(prose);

        exact.Should().NotBe(heuristic,
            "if the heuristic were accurate this whole type would be unnecessary");
        exact.Should().BeGreaterThan(0);
    }

    [Fact]
    public void EmptyContentIsZeroByEitherMethod()
    {
        new LongMemEvalTokenCounter("gpt-4o").Count("").Should().Be(0);
        new LongMemEvalTokenCounter("gpt-4o").Count(null).Should().Be(0);
        new LongMemEvalTokenCounter("unknown").Count("").Should().Be(0);
    }
}
