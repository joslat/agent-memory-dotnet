using System.Text.Json;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalEvidenceProjectionTests
{
    [Fact]
    public void CreateAcceptedResult_IdentifierModeRetainsSafeNormalizedEvidence()
    {
        var result = new ExternalBenchmarkResult
        {
            BenchmarkId = "benchmark-id",
            BenchmarkName = "benchmark-name",
            PerTypeResults = new Dictionary<string, TypeResult>(),
            OverallAccuracy = 100,
            TaskAveragedAccuracy = 100,
            Duration = TimeSpan.FromSeconds(1),
            Options = new ExternalBenchmarkOptions(),
            QuestionResults =
            [
                new QuestionResult
                {
                    QuestionId = "q-1",
                    QuestionType = "multi-session",
                    Question = "question-sentinel",
                    GoldAnswer = "gold-sentinel",
                    AgentResponse = "answer-sentinel",
                    Correct = true,
                    RawScore = 100,
                    Evidence = new QuestionEvidenceEnvelope
                    {
                        SchemaVersion = QuestionEvidenceEnvelope.CurrentSchemaVersion,
                        Retrieved =
                        [
                            new EvidenceReference
                            {
                                Id = "fact:safe-id",
                                Rank = 1,
                                SimilarityScore = 0.75,
                                SourceSessionId = "safe-session",
                                Content = "evidence-content-sentinel"
                            }
                        ]
                    },
                    EvidenceDiagnostics = new QuestionEvidenceDiagnostics
                    {
                        Status = EvidenceObservationStatus.Observed,
                        RetrievedReferenceCount = 1,
                        AnswerContextReferenceCount = 1,
                        DistinctSourceSessionCount = 1
                    },
                    Duration = TimeSpan.FromSeconds(1)
                }
            ]
        };

        var projection = LongMemEvalReportProjection.CreateAcceptedResult(
            result, LongMemEvalEvidenceDetail.Identifiers);
        var json = JsonSerializer.Serialize(projection);

        json.Should().Contain("\"Evidence\":")
            .And.Contain("fact:safe-id")
            .And.Contain("safe-session")
            .And.Contain("\"EvidenceDiagnostics\":")
            .And.NotContain("evidence-content-sentinel")
            .And.NotContain("question-sentinel")
            .And.NotContain("gold-sentinel")
            .And.NotContain("answer-sentinel");
    }

    [Fact]
    public void RankedEvidence_IdentifierModeOmitsNullContentProperty()
    {
        var evidence = new LongMemEvalRankedEvidence(
            MessageId: "message-1",
            RetrievalRank: 1,
            ContextRank: 1,
            SimilarityScore: 0.75,
            SourceSessionId: "session-1",
            SourceSessionOrdinal: 0,
            SourceTurnOrdinal: 0,
            SourceTimestamp: "2026-01-01T00:00:00Z",
            Role: "user",
            IsSyntheticBoundary: false,
            IsSyntheticFormatterPadding: false,
            GoldSessionHit: true,
            GoldTurnHit: true,
            Content: null);

        var json = JsonSerializer.Serialize(evidence);

        json.Should().NotContain("\"Content\":");
    }
}
