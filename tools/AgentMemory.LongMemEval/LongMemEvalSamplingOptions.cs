using System.Globalization;
using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

/// <summary>
/// The sampling and judge-protocol options that every verb drawing LongMemEval questions parses
/// identically.
/// </summary>
/// <remarks>
/// <para>
/// <b>These lived privately in the prepared-pair verb, so the reference arms never had them.</b> A
/// `--reference-arm` command given `--abstention target --abstention-proportion 0.4` accepted the flags,
/// ignored them, and drew a different 50 questions from the prepared pair it was meant to bracket. The
/// two sets had 4 of 50 questions in common, and nothing said so.
/// </para>
/// <para>
/// One parser with two callers, so the verbs cannot drift apart again. The defaults reproduce every
/// sealed measurement: as-sampled abstention, no proportion, and the free-text judge protocol.
/// </para>
/// </remarks>
internal static class LongMemEvalSamplingOptions
{
    /// <summary>Parses <c>--abstention exclude|as-sampled|only|target</c>.</summary>
    /// <remarks>
    /// Default <c>as-sampled</c>, which every recorded run used. Changing what a sample contains changes
    /// every number computed from it, so the default is preserved exactly rather than improved.
    /// </remarks>
    internal static AbstentionSamplingPolicy ParseAbstention(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "" or "as-sampled" => AbstentionSamplingPolicy.AsSampled,
            "exclude" => AbstentionSamplingPolicy.Exclude,
            "only" => AbstentionSamplingPolicy.Only,
            "target" => AbstentionSamplingPolicy.TargetProportion,
            _ => throw new ArgumentException(
                "--abstention must be one of: as-sampled, exclude, only, target."),
        };

    /// <summary>Parses <c>--abstention-proportion</c>, the share of the sample that must abstain.</summary>
    /// <remarks>
    /// Only meaningful with <c>--abstention target</c>. Rejected outside (0,1): a proportion of 0
    /// silently means "exclude" and 1 means "only", and expressing either by accident would produce a
    /// sample nobody chose.
    /// </remarks>
    internal static double? ParseAbstentionProportion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed is <= 0 or >= 1)
        {
            throw new ArgumentException(
                "--abstention-proportion must be strictly between 0 and 1; use --abstention exclude "
                + "or --abstention only for the endpoints.");
        }
        return parsed;
    }

    /// <summary>
    /// Parses <c>--judge-protocol</c>. Defaults to <c>FreeText</c>, the protocol every sealed base here
    /// was scored under.
    /// </summary>
    /// <remarks>
    /// <b>Never flipped silently.</b> StructuredJson is the real fix for a systematic free-text
    /// mis-scoring, and AgentEval's own documentation says results under it are not comparable with a
    /// free-text base. Changing the default would not produce a wrong number; it would produce a
    /// better one that silently invalidates every comparison anybody makes against the existing runs.
    /// </remarks>
    internal static JudgeVerdictProtocol ParseJudgeProtocol(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "free-text" or "freetext" => JudgeVerdictProtocol.FreeText,
        "structured-json" or "structuredjson" or "json" => JudgeVerdictProtocol.StructuredJson,
        _ => throw new ArgumentException(
            "--judge-protocol must be one of: free-text, structured-json."),
    };

    /// <summary>
    /// Parses <c>--judge-max-output-tokens</c>, the judge's output ceiling. Absent means the 256 every
    /// recorded run used.
    /// </summary>
    /// <remarks>
    /// A reasoning judge spends output tokens before it writes a verdict. At the provider default the
    /// GLM-5.3 family reasons on every call (the 2026-09-29 checkpoint measured the Flash model at about
    /// 55–60% of its output), so 256 can be spent before the verdict starts. An empty verdict then rejects
    /// the arm. This raises the ceiling; it does not change the judge prompt or the scoring.
    /// </remarks>
    internal static int ParseJudgeMaxOutputTokens(string? value)
    {
        if (value is null) return LongMemEvalBenchmarkProtocol.DefaultJudgeMaxOutputTokens;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new ArgumentException("--judge-max-output-tokens must be a positive integer.");
        return parsed;
    }
}
