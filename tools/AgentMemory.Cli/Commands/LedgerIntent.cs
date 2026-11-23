#pragma warning disable AMEXT001 // The product intent pack is an Extensibility preview pack.
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Intent;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// A task ledger read as product intent (CO-2d; the owner's test bed choice (b), 22 Nov 2026). Ledger-specific, so it lives in
/// this tool: the library's readers take the ledger as a file the host names, and this repository's ledger is kept elsewhere.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Each row with an id (1 to 64 characters) and a task is an intent requirement keyed by its id.</item>
/// <item>Its statement is its task and the owner's words about it: each part of its notes (the text before the first
/// <c>[YYYY-MM-DD]</c> mark, and each marked entry) that names the owner (<c>Owner</c> as a word), newest kept first when they
/// do not all fit. So a new task text or a new owner decision writes a new version of the requirement (a requirement- or
/// decision-level change), and nothing else does.</item>
/// <item>Its status is <c>accepted</c>: a row's progress (Pending, Doing, Done) and its progress notes are the work layer's,
/// on its work item, not a change to what it asks.</item>
/// <item>A row whose id is another row's id followed by lower-case letters (<c>CO-2b</c>, <c>CMP-2f</c>) decomposes that row
/// (<see cref="IntentPack.Decomposes"/>), stored on the child.</item>
/// </list>
/// </remarks>
internal static partial class LedgerIntent
{
    /// <summary>The longest statement a requirement keeps (the intent pack's <c>text</c>).</summary>
    internal const int MaximumText = 2000;

    /// <summary>The ledger's rows as intent requirements, from <paramref name="source"/> (such as <c>ledger:</c> and a hash).</summary>
    internal static ModuleSnapshot Read(string ledgerJson, string ledgerFile, string source)
    {
        ArgumentNullException.ThrowIfNull(ledgerJson);
        using var document = JsonDocument.Parse(ledgerJson);
        if (!document.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            throw new FormatException("A task ledger is a JSON object with a tasks array.");
        var rows = tasks.EnumerateArray()
            .Select(t => (Id: Text(t, "id"), Task: Text(t, "task"), Notes: Text(t, "notes")))
            .Where(t => t.Id is { Length: > 0 and <= 64 } && !string.IsNullOrWhiteSpace(t.Task))
            .DistinctBy(t => t.Id, StringComparer.Ordinal).ToList();
        var ids = rows.Select(r => r.Id!).ToHashSet(StringComparer.Ordinal);
        var records = rows.Select(row =>
        {
            var id = row.Id!;
            var properties = new Dictionary<string, object?>
            {
                ["reference"] = id, ["text"] = Statement(row.Task!, row.Notes), ["pattern"] = "free", ["status"] = "accepted",
                ["file"] = Clip(ledgerFile, 512), ["source"] = Clip(ledgerFile + "#" + id, 512),
            };
            var parent = Parent(id, ids);
            return new ModuleProposal
            {
                Type = IntentPack.RequirementType, Key = id,
                Properties = properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
                Links = parent is null ? [] : [new ProposedLink { Relation = IntentPack.Decomposes, Target = parent }],
                Source = new ProposalSource { Excerpt = Clip(ledgerFile + "#" + id, 512) },
            };
        }).OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        return new ModuleSnapshot { Module = IntentPack.Manifest, Source = source, Records = records.AsReadOnly() };
    }

    /// <summary>The row an id decomposes: the same id without its trailing lower-case letters, when the ledger holds it.</summary>
    internal static string? Parent(string id, IReadOnlySet<string> ids) =>
        TrailingLetters().Match(id) is { Success: true } m && m.Index > 0 && id[..m.Index] is var parent && parent != id && ids.Contains(parent) ? parent : null;

    /// <summary>A row's statement: its task, then the owner's words about it, newest kept first, within <see cref="MaximumText"/>.</summary>
    internal static string Statement(string task, string? notes)
    {
        var text = task.Trim();
        if (text.Length >= MaximumText) return text[..MaximumText];
        var kept = new List<string>();
        var room = MaximumText - text.Length - 2;
        foreach (var segment in OwnerSegments(notes).Reverse())
        {
            if (segment.Length + 1 > room) break;
            kept.Insert(0, segment);
            room -= segment.Length + 1;
        }
        return kept.Count == 0 ? text : text + "\n\n" + string.Join('\n', kept);
    }

    /// <summary>The parts of a row's notes that name the owner: the text before the first date mark, and each marked entry.</summary>
    internal static IReadOnlyList<string> OwnerSegments(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? []
        : [.. DateMark().Split(notes).Select(s => s.Trim()).Where(s => s.Length > 0 && OwnerWord().IsMatch(s))];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"[a-z]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingLetters();

    // Split before each "[2026-11-22]": the ledger's convention for an appended note.
    [GeneratedRegex(@"\s*(?=\[\d{4}-\d{2}-\d{2}\])", RegexOptions.CultureInvariant)]
    private static partial Regex DateMark();

    [GeneratedRegex(@"\bOwner\b", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerWord();
}
