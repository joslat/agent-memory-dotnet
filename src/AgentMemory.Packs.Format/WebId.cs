// SPDX-License-Identifier: Apache-2.0
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// A WebID as the federated profile accepts it (federated packs §3), by one explicit grammar that every verifier applies the
/// same way: <c>https://</c> and a host name of letters, digits, hyphens and dots (an IPv4 address matches it too; the Solid
/// adapter refuses IP addresses before any request), or <c>http://</c> for a loopback host only; an optional port; a path starting
/// with <c>/</c> of IRI characters that need no escaping in Turtle (no <c>&lt; &gt; " { } | \ ^ `</c>, spaces or non-ASCII),
/// at most one <c>#</c>; at most 512 characters. WebIDs are compared as exact strings: two spellings of one address are two
/// identities, so nothing is equal by normalisation.
/// </summary>
internal static partial class WebId
{
    internal const int MaximumLength = 512;

    internal static bool IsValid(string? text) =>
        text is { Length: > 0 and <= MaximumLength } && Grammar().IsMatch(text) && text.Count(c => c == '#') <= 1;

    /// <summary>The problem with a WebID named <paramref name="what"/>, or null.</summary>
    internal static string? Problem(string? text, string what) => IsValid(text)
        ? null
        : $"{what} is not a WebID: https and a host (http only for localhost), a path starting with '/', at most {MaximumLength} characters that need no escaping.";

    [GeneratedRegex("""\A(?:https://[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::[0-9]{1,5})?/[A-Za-z0-9\-._~:/?#@!$&'()*+,;=%]*\z""", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();
}
