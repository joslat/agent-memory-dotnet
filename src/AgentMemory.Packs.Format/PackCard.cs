using System.Text.Json;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The pack card (pack format v1.2 §15): who owns a pack, what it is for and what it is not for, where its content comes from,
/// the boundaries a host must keep with it, and the risks its publisher knows. <c>card.json</c> at the pack root, signed with
/// the pack like every listed file; optional, and read strictly by both verifiers when present: exactly these fields, each of
/// its type and length (characters, that is code points, as every text bound in the format counts: feature 02), and no control character but a line
/// feed or a tab, so a card shows the same everywhere and cannot drive a terminal.
/// </summary>
/// <param name="OwnerName">Who owns and answers for the pack.</param>
/// <param name="OwnerContact">How to reach the owner: a URL or an address, as text.</param>
/// <param name="IntendedUse">What the pack is for.</param>
/// <param name="Limitations">What it is not for, and where it may be wrong.</param>
/// <param name="Boundaries">What a host must not do with it.</param>
/// <param name="Risks">The risks its publisher knows.</param>
/// <param name="Provenance">Where its content comes from.</param>
/// <param name="Audience">Who it is meant for, in prose.</param>
/// <param name="Domain">Its domain, such as "software engineering".</param>
/// <param name="References">Further reading: URLs or citations, as text.</param>
internal sealed record PackCard(string OwnerName, string? OwnerContact, string IntendedUse, string Limitations, IReadOnlyList<string> Boundaries,
    IReadOnlyList<string> Risks, string? Provenance, string? Audience, string? Domain, IReadOnlyList<string> References)
{
    internal const string FileName = "card.json";
    internal const string Format = "agentmemory.pack.card/1";
    internal const int MaximumText = 2000;
    internal const int MaximumItem = 500;
    internal const int MaximumItems = 32;
    internal const int MaximumReferences = 16;

    /// <summary>Reads a card strictly, or returns why it is not one.</summary>
    internal static (PackCard? Card, string? Problem) Parse(byte[] bytes)
    {
        if (bytes.Length > PackVerifier.MaximumJsonBytes) return (null, $"{FileName} is larger than {PackVerifier.MaximumJsonBytes >> 20} MiB.");
        if (PackJson.Check(bytes) is { } malformed) return (null, $"{FileName} {malformed}.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return (null, $"{FileName} is not an object.");
        if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !PackRegistry.Shape("card").Contains(n)) is { } unknown)
            return (null, $"{FileName} has an unknown field '{unknown}'.");
        if (Str(root, "format") != Format) return (null, $"{FileName} must name format '{Format}'.");
        if (!root.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object
            || owner.EnumerateObject().Any(p => !PackRegistry.Shape("cardOwner").Contains(p.Name)))
            return (null, $"{FileName} names its owner as an object of 'name' and, optionally, 'contact'.");
        string? problem = null;
        var ownerName = Text(owner, "name", 200, required: true, ref problem, "owner.name");
        var contact = Text(owner, "contact", 200, required: false, ref problem, "owner.contact");
        var intended = Text(root, "intendedUse", MaximumText, required: true, ref problem);
        var limitations = Text(root, "limitations", MaximumText, required: true, ref problem);
        var provenance = Text(root, "provenance", MaximumText, required: false, ref problem);
        var audience = Text(root, "audience", MaximumItem, required: false, ref problem);
        var domain = Text(root, "domain", 100, required: false, ref problem);
        var boundaries = Items(root, "boundaries", MaximumItems, ref problem);
        var risks = Items(root, "risks", MaximumItems, ref problem);
        var references = Items(root, "references", MaximumReferences, ref problem);
        return problem is not null ? (null, problem)
            : (new PackCard(ownerName!, contact, intended!, limitations!, boundaries, risks, provenance, audience, domain, references), null);
    }

    private static string? Text(JsonElement element, string name, int maximum, bool required, ref string? problem, string? label = null)
    {
        if (problem is not null) return null;
        label ??= name;
        if (!element.TryGetProperty(name, out var value))
        {
            if (required) problem = $"{FileName} needs '{label}'.";
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || !IsText(text, maximum))
            problem = $"{FileName}: '{label}' is text of 1 to {maximum} characters, without control characters but a line feed or a tab.";
        return problem is null ? value.GetString() : null;
    }

    private static IReadOnlyList<string> Items(JsonElement element, string name, int maximum, ref string? problem)
    {
        if (problem is not null || !element.TryGetProperty(name, out var value)) return [];
        var items = new List<string>();
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= maximum)
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || !IsText(text, MaximumItem)) break;
                items.Add(text);
            }
        if (value.ValueKind != JsonValueKind.Array || items.Count != value.GetArrayLength() || items.Distinct(StringComparer.Ordinal).Count() != items.Count)
            problem = $"{FileName}: '{name}' is a list of at most {maximum} distinct texts of 1 to {MaximumItem} characters each.";
        return problem is null ? items : [];
    }

    /// <summary>1 to <paramref name="maximum"/> characters (code points), display-safe (feature 04: no control but a line feed or a tab, no format character but ZWNJ and ZWJ, no private use).</summary>
    internal static bool IsText(string text, int maximum) => PackText.IsDisplayText(text, maximum, line: false);

    /// <summary>As <see cref="IsText"/>, on one line: no line feed, tab, line or paragraph separator.</summary>
    internal static bool IsLine(string text, int maximum) => PackText.IsDisplayText(text, maximum, line: true);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
