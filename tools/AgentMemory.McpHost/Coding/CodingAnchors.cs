#pragma warning disable AMEXT001 // Coding mode composes the Extensibility preview.
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.McpHost.Coding;

/// <summary>An anchor as the coding API receives it: <c>{ "kind": "path" | "symbol", "value": "…" }</c>.</summary>
internal sealed record CodingAnchorInput
{
    public required string Kind { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// The files and symbols of the work in hand (coding packs CMP-1): validated from the API, or found by the hook client in the
/// prompt and the working tree. Only anchors that match a pack's records reach anything; the rest cost one indexed read.
/// </summary>
internal static partial class CodingAnchors
{
    // A bound on the request, not the walk's limit: the walk takes the first 32 distinct valid anchors, as every host does
    // (review 4: refusing a request of 33, or a value blank by Unicode's white space, made this host differ from the others).
    public const int Maximum = 256;
    public const int MaximumLength = 512;
    private const int FromPromptMaximum = 12;

    /// <summary>Validated anchors from the API; null or empty is none.</summary>
    public static IReadOnlyList<ContextAnchor> From(IReadOnlyList<CodingAnchorInput>? input)
    {
        if (input is null || input.Count == 0) return [];
        if (input.Count > Maximum) throw new ArgumentException($"At most {Maximum} anchors a request.");
        // As given: the walk drops blank, long and unpaired-surrogate values, takes each in NFC, and removes duplicates.
        return [.. input.Select(a =>
        {
            if (a.Value is null) throw new ArgumentException("An anchor has a value.");
            return a.Kind switch
            {
                "path" => ContextAnchor.Path(a.Value, "api"),
                "symbol" => ContextAnchor.Symbol(a.Value, "api"),
                _ => throw new ArgumentException("An anchor kind is path or symbol."),
            };
        })];
    }

    /// <summary>The question a governs call asks: the anchors themselves, so the gate weighs what governs them.</summary>
    public static string Question(IReadOnlyList<ContextAnchor> anchors) =>
        "Before changing " + string.Join(", ", anchors.Take(8).Select(a => a.Value)) + ": what decisions, requirements, tests and lessons govern it?";

    /// <summary>
    /// Paths and code names a prompt mentions: tokens with a folder separator or a source file extension, and identifiers with
    /// two or more capitals (<c>RemovePackAsync</c>, <c>Neo4jModuleStore</c>). At most twelve, in order of first mention.
    /// </summary>
    public static IReadOnlyList<ContextAnchor> FromPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return [];
        var found = new List<ContextAnchor>();
        foreach (Match match in Token().Matches(prompt))
        {
            var token = match.Value.Trim('`', '\'', '"', '(', ')', '[', ']', ',', ';', ':', '.');
            if (token.Length is < 3 or > MaximumLength) continue;
            ContextAnchor? anchor = PathLike().IsMatch(token) ? ContextAnchor.Path(token, "prompt")
                : Identifier().IsMatch(token) ? ContextAnchor.Symbol(token, "prompt") : null;
            if (anchor is not null && !found.Any(a => a.Kind == anchor.Kind && a.Value == anchor.Value)) found.Add(anchor);
            if (found.Count == FromPromptMaximum) break;
        }
        return found;
    }

    /// <summary>Changed and untracked files from <c>git status --porcelain=v1</c>, repository-relative; renames keep the new path.</summary>
    public static IReadOnlyList<ContextAnchor> FromStatus(string porcelain, int maximum = 16)
    {
        var found = new List<ContextAnchor>();
        foreach (var line in porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 3) continue;
            var path = line[2..].Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) path = path[(arrow + 4)..];
            path = path.Trim('"');
            if (path.Length is 0 or > MaximumLength || path.EndsWith('/')) continue;
            found.Add(ContextAnchor.Path(path, "working-tree"));
            if (found.Count == maximum) break;
        }
        return found;
    }

    [GeneratedRegex(@"[`'""(\[]?[A-Za-z0-9_./\\-]+[`'"")\],;:.]?", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"^(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+$|^[A-Za-z0-9_-]+\.(?:cs|csproj|fs|vb|ts|tsx|js|py|java|kt|go|rs|cpp|c|h|json|yml|yaml|md|sql|cypher)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();

    // Two or more capitals and a lower-case letter: code names (RemovePackAsync), not shouted words or plain sentences.
    [GeneratedRegex(@"^(?=(?:[^A-Z]*[A-Z]){2})(?=.*[a-z])[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
