using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

internal static class LongMemEvalBenchmarkProtocol
{
    internal static ExternalBenchmarkOptions CreateOptions(
        string datasetPath,
        int questions,
        int seed,
        int judgeRetryAttempts,
        LongMemEvalEvidenceDetail evidenceDetail,
        int maxRelevantMessages) =>
        new()
        {
            DatasetPath = datasetPath,
            MaxQuestions = questions,
            StratifiedSampling = true,
            RandomSeed = seed,
            PreserveSessionBoundaries = true,
            IncludeTimestamps = true,
            HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
            DatasetMode = "S",
            JudgeFailurePolicy = JudgeFailurePolicy.RetryThenInconclusive,
            MaxJudgeRetries = judgeRetryAttempts,
            JudgeTemperature = null,
            JudgeMaxOutputTokens = 256,
            JudgeEvidenceMode = JudgeEvidenceMode.Outcome,
            EvidenceCaptureMode = evidenceDetail switch
            {
                LongMemEvalEvidenceDetail.None => EvidenceCaptureMode.None,
                LongMemEvalEvidenceDetail.Identifiers => EvidenceCaptureMode.References,
                LongMemEvalEvidenceDetail.Content => EvidenceCaptureMode.Full,
                _ => throw new ArgumentOutOfRangeException(nameof(evidenceDetail))
            },
            EvidenceTopK = maxRelevantMessages
        };

    internal static IReadOnlyList<(string UserMessage, string AssistantResponse)> History(
        LongMemEvalEvidenceQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (question.Messages.Count == 0 || question.Messages.Count % 2 != 0)
        {
            throw new InvalidOperationException(
                $"LongMemEval question {question.QuestionId} has an invalid formatted-message count.");
        }

        var result = new List<(string UserMessage, string AssistantResponse)>(
            question.Messages.Count / 2);
        for (var index = 0; index < question.Messages.Count; index += 2)
        {
            var user = question.Messages[index];
            var assistant = question.Messages[index + 1];
            if (!string.Equals(user.Role, "user", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assistant.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"LongMemEval question {question.QuestionId} has invalid formatted role ordering.");
            }

            result.Add((user.FormattedContent, assistant.FormattedContent));
        }

        return result.AsReadOnly();
    }
}
