using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// <c>--extraction-reasoning default|low|medium|high</c>: the reasoning effort requested on extraction
/// calls, and on nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured before it was added.</b> The 2026-09-29 Bitdeer checkpoint sent no reasoning setting.
/// GLM-5.3-Flash then spent about 55–60% of its ~6,300 output tokens per call reasoning, at a median
/// of 27 s per call, to fill a JSON schema. At effort Low the same model measured 7.8 s per call (median)
/// on the chosen-10 run of the same day, with about 1,700 output tokens instead of 6,300. The effort is set
/// on the extraction <see cref="IChatClient"/>, so the library needs no change.
/// </para>
/// <para>
/// <b>Part of the corpus identity.</b> The effort changes what the extractor writes, so it is appended
/// to the extraction identity sealed into the manifest (<c>+reasoning-low</c>). A reuse under a
/// different effort is then refused as drift instead of silently measuring the wrong corpus. Without the
/// flag the client is returned unwrapped and the identity unchanged, so every earlier corpus still
/// matches.
/// </para>
/// <para>
/// No output-token cap is added: a batch emits thousands of tokens of JSON, and a cap would truncate
/// it. <c>none</c> is not offered, because Bitdeer answers HTTP 400 to it.
/// </para>
/// </remarks>
internal static class LongMemEvalExtractionReasoning
{
    internal const string Option = "--extraction-reasoning";

    /// <summary>Null means the provider default: nothing is sent.</summary>
    internal static ReasoningEffort? Parse(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "default" => null,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        _ => throw new ArgumentException($"{Option} must be one of: default, low, medium, high."),
    };

    /// <summary>
    /// The extraction client with the effort applied. Without an effort, the client itself, untouched.
    /// </summary>
    /// <remarks>
    /// <c>??=</c>, so a caller that sets its own reasoning options keeps them. The library's extraction
    /// runner sets none today.
    /// </remarks>
    internal static IChatClient Apply(IChatClient client, ReasoningEffort? effort)
    {
        ArgumentNullException.ThrowIfNull(client);
        return effort is { } requested
            ? client.AsBuilder()
                .ConfigureOptions(options => options.Reasoning ??= new ReasoningOptions { Effort = requested })
                .Build()
            : client;
    }

    /// <summary>The extraction identity, with the effort appended when one is requested.</summary>
    internal static string Identity(string extractionIdentity, ReasoningEffort? effort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extractionIdentity);
        return effort is null ? extractionIdentity : $"{extractionIdentity}+reasoning-{Token(effort)}";
    }

    /// <summary>The value the report records: the effort, or <c>provider-default</c>.</summary>
    internal static string Token(ReasoningEffort? effort) =>
        effort?.ToString().ToLowerInvariant() ?? "provider-default";
}
