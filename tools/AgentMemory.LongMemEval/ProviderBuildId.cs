using System.Reflection;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Recovers the provider's backend build identifier — OpenAI calls it <c>system_fingerprint</c> — from a
/// chat response, when the provider returned one (S-4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters more here than anywhere else in the project.</b> This deployment rejects
/// <c>temperature: 0</c>, so extraction is nondeterministic: three cold builds of an *identical*
/// configuration shared 7.5% of their stored triples and scored 25 points apart. The provider's
/// determinism guarantee is conditional on its backend build being unchanged, and a model pinned by
/// deployment name is <b>not</b> pinned by build. So this value is the only thing that can tell a reader
/// two runs were never comparable — rather than leaving every difference between them attributable to
/// the change under test.
/// </para>
/// <para>
/// <b>Absence is reported as null, never as a placeholder.</b> "The provider did not report a build" and
/// "the build was X" are different facts. A sentinel would let a report deny an incomparability it
/// cannot actually rule out, which is worse than admitting the value is unavailable.
/// </para>
/// <para>
/// <b>Why this duplicates AgentEval.</b> `AgentEval.Memory.External.ProviderFingerprint` implements
/// exactly this and is <c>internal</c>, and this harness holds no <c>InternalsVisibleTo</c> grant. The
/// upstream ask (S-4) is therefore narrower than the plan recorded: not "build this" but "make the
/// existing type public". Until then, ~30 lines here beat leaving every recorded run unable to say
/// which backend produced it. Delete this in favour of the package type the moment it is exposed.
/// </para>
/// </remarks>
internal static class ProviderBuildId
{
    /// <summary>Keys checked in a response's additional-properties bag, in order.</summary>
    private static readonly string[] CandidateKeys =
        ["system_fingerprint", "systemFingerprint", "SystemFingerprint"];

    /// <summary>Property names checked on the provider-specific raw response, in order.</summary>
    private static readonly string[] CandidateRawProperties =
        ["SystemFingerprint", "system_fingerprint"];

    /// <summary>
    /// Upper bound on a retained value. Providers return short opaque tokens; anything longer is not a
    /// build id and must not be copied into a report verbatim.
    /// </summary>
    private const int MaximumLength = 128;

    /// <summary>
    /// Reads the build id from a chat response, preferring the additional-properties bag and falling
    /// back to reflection over the provider's raw response object. Null when none was supplied.
    /// </summary>
    /// <remarks>
    /// <c>ChatResponse</c> has no <c>SystemFingerprint</c> member, so there are exactly two places the
    /// value can be: the loosely-typed properties bag, or the provider's own response object (for the
    /// OpenAI client a <c>ChatCompletion</c>, which does carry it). Reflection is confined to the second
    /// and is failure-tolerant on purpose — a provider that shapes its response differently must leave
    /// this returning null, not throw in the middle of a measured run.
    /// </remarks>
    internal static string? FromChatResponse(ChatResponse? response)
    {
        if (response is null) return null;

        if (response.AdditionalProperties is { Count: > 0 } properties)
        {
            foreach (var key in CandidateKeys)
            {
                if (properties.TryGetValue(key, out var value) && Normalize(value?.ToString()) is { } found)
                    return found;
            }
        }

        return FromRawRepresentation(response.RawRepresentation);
    }

    private static string? FromRawRepresentation(object? raw)
    {
        if (raw is null) return null;

        foreach (var name in CandidateRawProperties)
        {
            try
            {
                var property = raw.GetType().GetProperty(
                    name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetValue(raw)?.ToString() is { } value && Normalize(value) is { } found)
                    return found;
            }
            catch (Exception exception) when (
                exception is TargetInvocationException or MethodAccessException
                    or AmbiguousMatchException or NotSupportedException)
            {
                // A provider object that refuses reflection reports "no build id", which is the honest
                // answer. Throwing here would abort a run over telemetry.
            }
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > MaximumLength ? null : trimmed;
    }
}
