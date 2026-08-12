using System.Net;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// A provider that refuses some content must not cost the whole preparation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, 2026-08-12.</b> A 50-question cold build died after <b>270 of 616</b> extraction calls
/// because two sessions of a public research dataset tripped an Azure content policy. The batch-split
/// path treated the 400 as a shape failure, halved the batch, re-sent the same text to the same
/// filter, and propagated once the batch reached size 1 — losing an hour of work to content that was
/// never going to be accepted.
/// </para>
/// <para>
/// A refusal is <b>terminal</b>: neither a retry nor a split changes the text or the policy. The only
/// useful responses are to skip the affected sessions or to abandon the run, and skipping is
/// overwhelmingly better provided the loss is <b>recorded</b> — a corpus with gaps that looks complete
/// would attribute those gaps to recall.
/// </para>
/// </remarks>
public sealed class ContentRefusalResilienceTests
{
    // HttpRequestException rather than ClientResultException: TryGetStatus reads the status
    // reflectively from either, and the test should not take a provider package dependency to
    // classify an error the production code deliberately classifies without one.
    private static Exception Refusal(string body) =>
        new HttpRequestException($"HTTP 400 ({body})", null, HttpStatusCode.BadRequest);

    private static Exception Status(int code) =>
        new HttpRequestException($"HTTP {code} (something else)", null, (HttpStatusCode)code);

    [Theory]
    [InlineData("content_filter")]
    [InlineData("invalid_request_error: cyber_policy")]
    [InlineData("ResponsibleAIPolicyViolation")]
    [InlineData("The response was filtered due to the content filter")]
    public void TheProvidersRefusalVocabularyIsRecognised(string body)
    {
        // Matched on the provider's own words, not on status alone: 400 covers both "your request is
        // malformed" -- which splitting legitimately diagnoses -- and "I will not process this",
        // which it cannot.
        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(Refusal(body)).Should().BeTrue();
    }

    [Fact]
    public void ARefusalIsNotTreatedAsABatchShapeFailure()
    {
        // THE fix. Classifying it as a shape failure is what made the extractor split, re-send the
        // same text to the same policy, and eventually propagate.
        var refusal = Refusal("content_filter");

        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(refusal).Should().BeTrue();
        LlmMultiSessionUnifiedMemoryExtractor.IsBatchShapeFailure(refusal).Should().BeFalse();
    }

    [Fact]
    public void AnOrdinaryBadRequestIsStillASplittableShapeFailure()
    {
        // The other direction, and the one that must not regress: a genuine 400 -- an over-long batch,
        // a malformed schema -- is exactly what halving the batch diagnoses.
        var malformed = Status(400);

        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(malformed).Should().BeFalse();
        LlmMultiSessionUnifiedMemoryExtractor.IsBatchShapeFailure(malformed).Should().BeTrue();
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void TransientAndServerFailuresAreNeitherRefusalsNorShapeFailures(int status)
    {
        // A timeout, a throttle or a server error may succeed on the same text later, so neither
        // skipping the content nor splitting the batch is the right response.
        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(Status(status)).Should().BeFalse();
        LlmMultiSessionUnifiedMemoryExtractor.IsBatchShapeFailure(Status(status)).Should().BeFalse();
    }

    [Fact]
    public void AFormatFailureRemainsASplittableShapeFailure()
    {
        LlmMultiSessionUnifiedMemoryExtractor.IsBatchShapeFailure(new FormatException("bad json"))
            .Should().BeTrue();
        LlmMultiSessionUnifiedMemoryExtractor.IsContentRejection(new FormatException("bad json"))
            .Should().BeFalse();
    }

    [Fact]
    public void ARefusalIsRecordedWithTheSessionsItCost()
    {
        // "2 refusals" says nothing about whether the corpus is usable. "2 refusals costing 6 of
        // 2,418 sessions" does, and it is the session count that the tolerance and the manifest need.
        var diagnostics = new LlmExtractionBatchDiagnostics();

        diagnostics.RecordContentRejection(Refusal("content_filter"), sourceSessions: 4);
        diagnostics.RecordContentRejection(Refusal("content_filter"), sourceSessions: 2);

        var snapshot = diagnostics.Snapshot();
        snapshot.ContentRejections.Should().Be(2);
        snapshot.SessionsRefused.Should().Be(6);
    }

    [Fact]
    public void RefusalsAreCountedSeparatelyFromSplits()
    {
        // Different events with different remedies: a split is a recoverable shape problem, a refusal
        // is terminal for that text. Folding them together would hide a corpus losing content behind
        // a counter that looks like ordinary batching noise.
        var diagnostics = new LlmExtractionBatchDiagnostics();

        diagnostics.RecordSplit(new FormatException("bad json"), sourceSessions: 4);
        diagnostics.RecordContentRejection(Refusal("content_filter"), sourceSessions: 1);

        var snapshot = diagnostics.Snapshot();
        snapshot.Splits.Should().Be(1);
        snapshot.ContentRejections.Should().Be(1);
        snapshot.Details.Should().Contain(d => d.Reason == "content-rejected");
    }

    [Fact]
    public void ADeltaSubtractsRefusalsToo()
    {
        // The harness reconciles per-question deltas against a baseline. A field that did not subtract
        // would report every earlier refusal again on every subsequent question.
        var diagnostics = new LlmExtractionBatchDiagnostics();
        diagnostics.RecordContentRejection(Refusal("content_filter"), sourceSessions: 3);
        var baseline = diagnostics.Snapshot();

        diagnostics.RecordContentRejection(Refusal("content_filter"), sourceSessions: 2);
        var delta = diagnostics.Snapshot().Delta(baseline);

        delta.ContentRejections.Should().Be(1);
        delta.SessionsRefused.Should().Be(2);
    }
}
