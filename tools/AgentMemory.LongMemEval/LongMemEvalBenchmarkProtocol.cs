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
        int maxRelevantMessages,
        JudgeVerdictProtocol verdictProtocol = JudgeVerdictProtocol.FreeText) =>
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
            // Raw, not Outcome. In Outcome mode AgentEval renders the explanation as
            // $"Judge outcome: {status}" -- the status as a STRING -- and we then parsed that string
            // back into a status, reporting the failed round trip as "the judge returned no valid
            // verdict". The judge was not the problem; discarding its reasoning and re-deriving it
            // from our own rendering was. Raw keeps the model's actual text (bounded to 4096 chars by
            // AgentEval), which is the only way to tell a WRONG judge from an UNPARSEABLE one.
            JudgeEvidenceMode = JudgeEvidenceMode.Raw,
            // Decouples "what we keep" from "what we render". Today JudgeEvidenceMode.Raw already
            // retains the model's text, so this changes nothing -- it exists so that a later move to a
            // shorter evidence mode cannot silently take the raw response away with it, which is the
            // only thing that distinguishes a WRONG judge from an UNPARSEABLE one.
            RetainRawJudgeResponse = true,
            // FreeText by default, deliberately. StructuredJson is strictly better at the job --
            // AgentEval's own docs name the free-text failure mode as vetoing a leading "yes" when the
            // word "no" appears later in the response, which is exactly the systematic failure seen on
            // question 7405e8b1 -- but the same docs are explicit that enabling it "changes verdicts on
            // the questions the free-text parser mis-scored" and therefore "makes results
            // non-comparable with a base recorded under the free-text protocol".
            //
            // Every sealed base in this project was recorded under FreeText. Flipping this default
            // would silently invalidate all of them, so it is a per-run decision the caller states out
            // loud rather than something inherited.
            JudgeVerdictProtocol = verdictProtocol,
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
