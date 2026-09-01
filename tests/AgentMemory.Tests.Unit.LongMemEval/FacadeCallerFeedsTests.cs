using System.Reflection;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Every option the runner accepts must be either FED by our facade or declared as deliberately left
/// at its default — the caller-feeds standing check.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape this exists to catch.</b> "Reachable is not the same as fed" has now cost this
/// project on four separate codepaths: an extraction lever no harness set, a renderer no harness set,
/// a public write verb no pipeline calls, and — the one that prompted this test —
/// <c>EvidenceCaptureMode</c>, which <c>TypedMemEvalOptionMapping</c> faithfully passes through to the
/// runner while <c>BuildFacade</c> never set it. So <c>--evidence-detail</c> configured our adapter
/// and left the runner on its default, and the flag looked wired because half of it was.
/// </para>
/// <para>
/// <b>Why a declared map rather than a clever diff.</b> Comparing a built facade against a default
/// one cannot tell "fed with a value that happens to equal the default" from "never fed" — and that
/// ambiguity is precisely the bug. So every property must be classified BY HAND, with a reason, and a
/// property nobody has classified fails this test. The cost is one line when the runner gains an
/// option; the alternative is discovering it during a paid run.
/// </para>
/// </remarks>
public class FacadeCallerFeedsTests
{
    /// <summary>Set by <c>BuildFacade</c> from a run option.</summary>
    private static readonly HashSet<string> Fed = new(StringComparer.Ordinal)
    {
        "MaxQuestions", "RandomSeed", "AnswerSeed", "TemporalGrounding", "ControlArm",
        // Added when this test was written: --evidence-detail previously reached the adapter only.
        "EvidenceCaptureMode", "EvidenceTopK",
    };

    /// <summary>Deliberately left at the runner's default, with the reason recorded.</summary>
    private static readonly Dictionary<string, string> IntentionallyDefaulted = new(StringComparer.Ordinal)
    {
        ["AnswerTemperature"] = "answer determinism is controlled through the chat client, not here",
        ["HistoryInjectionMode"] = "the adapter implements the timestamped interface; the runner picks",
        ["IncludeTimestamps"] = "subsumed by TemporalGrounding, which this facade does set",
        ["JudgeTemperature"] = "judge configuration is AgentEval's to own; we grade with their defaults",
        ["JudgeMaxOutputTokens"] = "as above",
        ["MaxJudgeRetries"] = "as above — and a retry count we chose would make our scores incomparable",
        ["JudgeFailurePolicy"] = "as above",
        ["JudgeEvidenceMode"] = "as above",
        ["RetainRawJudgeResponse"] = "raw judge text is theirs; our artifacts carry verdicts, not prose",
    };

    [Fact]
    public void EveryRunnerOptionIsEitherFedOrDeclaredDefaulted()
    {
        var settable = typeof(TypedMemEvalOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .ToArray();

        var classified = Fed.Concat(IntentionallyDefaulted.Keys).ToHashSet(StringComparer.Ordinal);

        settable.Should().BeSubsetOf(classified,
            "an option nobody has classified is an option nobody has decided about — which is how "
            + "EvidenceCaptureMode ended up passed through to the runner and never set");

        classified.Should().BeSubsetOf(settable,
            "a classification for an option that no longer exists is stale and will mislead the next "
            + "reader into thinking it was considered");
    }

    [Fact]
    public void TheEvidenceFlagReachesTheRunnerAndNotOnlyTheAdapter()
    {
        // The concrete regression. Before the fix this property was default regardless of the flag.
        foreach (var (detail, expected) in new[]
        {
            (LongMemEvalEvidenceDetail.None, EvidenceCaptureMode.None),
            (LongMemEvalEvidenceDetail.Identifiers, EvidenceCaptureMode.References),
            (LongMemEvalEvidenceDetail.Content, EvidenceCaptureMode.Full),
        })
        {
            LongMemEvalBenchmarkProtocol.CaptureModeFor(detail).Should().Be(expected);
        }
    }
}
