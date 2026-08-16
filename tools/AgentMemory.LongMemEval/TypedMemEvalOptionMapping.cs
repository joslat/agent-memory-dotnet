using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Replicates AgentEval's internal <c>TypedMemEvalOptions.ToExternalOptions</c> mapping, so the
/// evidence index selects and formats exactly the entries the runner will inject.
/// </summary>
/// <remarks>
/// <para>
/// The runner maps its facade onto <see cref="ExternalBenchmarkOptions"/> through an
/// <b>internal</b> method, so this harness cannot call it — but the evidence index must be built
/// from the same selection (MaxQuestions, RandomSeed, stratification) and the same formatting
/// (session boundaries, timestamps, grounding), or fingerprints will never align and every typed
/// run degrades to attribution <c>Unobserved</c>. The invariants replicated here are the family's
/// own, stated in the facade's documentation: stratified sampling on, session boundaries
/// preserved, the corpus id as the dataset mode (with a <c>-control</c> suffix for the control
/// arm), the vertical's required grounding unless overridden, and TextBlob injection forced up to
/// structured whenever grounding is active.
/// </para>
/// <para>
/// Drift is guarded, not hoped away: a test invokes the real internal mapper by reflection and
/// asserts property-for-property equality with this replica, so an AgentEval release that changes
/// the mapping fails a named test instead of silently mis-aligning the index.
/// </para>
/// </remarks>
internal static class TypedMemEvalOptionMapping
{
    internal static ExternalBenchmarkOptions ToExternalOptions(
        TypedMemEvalOptions facade,
        TypedMemEvalVerticalDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(descriptor);

        var grounding = facade.TemporalGrounding ?? descriptor.RequiredGrounding;
        var injection = grounding != TemporalGroundingMode.None &&
                        facade.HistoryInjectionMode == HistoryInjectionMode.TextBlob
            ? HistoryInjectionMode.StructuredChatHistory
            : facade.HistoryInjectionMode;

        return new ExternalBenchmarkOptions
        {
            MaxQuestions = facade.MaxQuestions,
            RandomSeed = facade.RandomSeed,
            StratifiedSampling = true,
            PreserveSessionBoundaries = true,
            IncludeTimestamps = facade.IncludeTimestamps,
            AnswerTemperature = facade.AnswerTemperature,
            AnswerSeed = facade.AnswerSeed,
            TemporalGrounding = grounding,
            HistoryInjectionMode = injection,
            DatasetMode = facade.ControlArm
                ? $"{descriptor.CorpusId}-control"
                : descriptor.CorpusId,
            DatasetPath = null,
            RunProvenanceMode = RunProvenanceMode.Full,
            JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
            JudgeTemperature = facade.JudgeTemperature,
            JudgeMaxOutputTokens = facade.JudgeMaxOutputTokens,
            MaxJudgeRetries = facade.MaxJudgeRetries,
            JudgeFailurePolicy = facade.JudgeFailurePolicy,
            JudgeEvidenceMode = facade.JudgeEvidenceMode,
            RetainRawJudgeResponse = facade.RetainRawJudgeResponse,
            EvidenceCaptureMode = facade.EvidenceCaptureMode,
            EvidenceTopK = facade.EvidenceTopK
        };
    }
}
