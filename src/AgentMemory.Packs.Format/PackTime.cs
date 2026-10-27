using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The one form a time takes in a pack and in a host's trust policy (PACK-conformance-v12): an RFC 3339 date-time, with
/// <c>Z</c> or an offset and at most seven fraction digits. A lenient parser in one language and a strict one in another
/// would read the same <c>validUntil</c> differently; the Python verifier holds the same grammar.
/// </summary>
internal static partial class PackTime
{
    /// <summary>Parses an RFC 3339 date-time; false for anything else.</summary>
    internal static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is not null && Rfc3339().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out value);
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339();
}
