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
        JudgeVerdictProtocol verdictProtocol = JudgeVerdictProtocol.FreeText,
        IReadOnlyList<string>? includeQuestionTypes = null,
        AbstentionSamplingPolicy abstentionPolicy = AbstentionSamplingPolicy.AsSampled,
        double? abstentionTargetProportion = null,
        bool suppressSyntheticBoundaries = false) =>
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
            // Type-filtered sampling (0.20). Null/empty reproduces the stratified-across-all-6 default
            // exactly. This is what makes a per-memory-type claim possible at all: a 50-question
            // stratified sample yields ~6 single-session-assistant questions, and 6 cannot carry one.
            IncludeQuestionTypes = includeQuestionTypes is { Count: > 0 }
                ? includeQuestionTypes.ToList()
                : null,
            // Abstention (0.20). AsSampled is the pre-0.20 behaviour. Across 52 recorded runs of ours,
            // NOT ONE _abs question ever ran -- and nothing said so, which is why the realised count now
            // travels on the result rather than being inferred.
            AbstentionPolicy = abstentionPolicy,
            AbstentionTargetProportion = abstentionTargetProportion,
            // Full provenance (0.20): dataset SHA-256, judge-prompt fingerprint, AgentEval version and a
            // fingerprint of the selected question ids. Sealed-base comparability was previously checked
            // by hand-diffing AgentEval's source between releases; now it is mechanical.
            RunProvenanceMode = RunProvenanceMode.Full,
            // Synthetic session boilerplate (0.20). For two failing questions, 30 of 30 retrieved
            // messages were this boilerplate, and it read as a defect in OUR retrieval for weeks.
            // Empty marker == omit. Default keeps the historical text so sealed bases stay comparable.
            SyntheticTurnMarker = suppressSyntheticBoundaries ? string.Empty : null,
            EvidenceCaptureMode = CaptureModeFor(evidenceDetail),
            EvidenceTopK = maxRelevantMessages
        };

    /// <summary>Maps our evidence-detail flag onto the runner's capture mode.</summary>
    /// <remarks>
    /// Shared rather than duplicated because it was duplicated by omission once already: the
    /// typedmemeval verb's facade never set <c>EvidenceCaptureMode</c> at all, so `--evidence-detail`
    /// fed the adapter and left the runner on its default. One definition, two callers.
    /// </remarks>
    internal static EvidenceCaptureMode CaptureModeFor(LongMemEvalEvidenceDetail evidenceDetail) =>
        evidenceDetail switch
        {
            LongMemEvalEvidenceDetail.None => EvidenceCaptureMode.None,
            LongMemEvalEvidenceDetail.Identifiers => EvidenceCaptureMode.References,
            LongMemEvalEvidenceDetail.Content => EvidenceCaptureMode.Full,
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceDetail))
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
