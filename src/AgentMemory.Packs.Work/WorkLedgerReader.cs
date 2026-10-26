using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;

namespace AgentMemory.Packs.Work;

/// <summary>One commit as the work pack reads it: its subject and the files it changed.</summary>
[Experimental("AMEXT001")]
public sealed record WorkCommit(string Sha, string Subject, IReadOnlyList<string> Files);

/// <summary>
/// Reads a task ledger and a repository's commits into a work snapshot (CODING-hierarchy). Every ledger task is one work item;
/// a commit belongs to the items its subject names before the first colon ("CMP-2a, CMP-3: ..."), and each item links to the
/// source files its commits changed, most often changed first, at most <see cref="MaximumFiles"/>.
/// </summary>
/// <remarks>
/// The ledger is a JSON object whose <c>tasks</c> hold <c>id</c>, <c>task</c>, <c>status</c> and optionally <c>phase</c>,
/// <c>notes</c>, <c>remaining</c>, <c>actual</c> and <c>checkpoint</c>. The code map's import writes the files; a link to a file
/// the code map does not hold is left out by the store and counted.
/// </remarks>
[Experimental("AMEXT001")]
public static class WorkLedgerReader
{
    /// <summary>Most files one work item links to.</summary>
    public const int MaximumFiles = 64;
    /// <summary>How much of an item's notes it keeps as its summary.</summary>
    public const int SummaryLength = 1200;

    /// <summary>Builds the snapshot. <paramref name="sourceFiles"/>, when given, keeps only links to those paths.</summary>
    public static ModuleSnapshot Read(string ledgerJson, string ledgerFile, IReadOnlyList<WorkCommit> commits, string source,
        IReadOnlySet<string>? sourceFiles = null)
    {
        ArgumentNullException.ThrowIfNull(ledgerJson); ArgumentNullException.ThrowIfNull(commits);
        using var document = JsonDocument.Parse(ledgerJson);
        if (!document.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            throw new FormatException("A task ledger is a JSON object with a tasks array.");
        var items = tasks.EnumerateArray().Where(t => Text(t, "id") is { Length: > 0 } && Text(t, "task") is { Length: > 0 }).ToList();
        var ids = items.Select(t => Text(t, "id")!).ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commit in commits)
            foreach (var id in Ids(commit.Subject, ids))
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                var changed = files.TryGetValue(id, out var known) ? known : files[id] = new(StringComparer.Ordinal);
                foreach (var file in commit.Files.Select(Normalize).Where(f => sourceFiles is null || sourceFiles.Contains(f)))
                    changed[file] = changed.GetValueOrDefault(file) + 1;
            }
        var records = items.DistinctBy(t => Text(t, "id"), StringComparer.Ordinal).Select(task =>
        {
            var id = Text(task, "id")!;
            var properties = new Dictionary<string, object?>
            {
                ["item"] = Clip(id, 64), ["title"] = Clip(Text(task, "task"), 300), ["status"] = Clip(Text(task, "status") ?? "unknown", 32),
                ["phase"] = Clip(Text(task, "phase"), 200), ["summary"] = Clip(Text(task, "notes"), SummaryLength),
                ["remaining"] = Clip(Text(task, "remaining"), 64), ["actual"] = Clip(Text(task, "actual"), 64),
                ["checkpoint"] = Clip(Text(task, "checkpoint"), 120), ["commits"] = counts.GetValueOrDefault(id),
                ["source"] = Clip(ledgerFile + "#" + id, 512),
            };
            var links = files.TryGetValue(id, out var changed)
                ? changed.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(MaximumFiles)
                    .Select(f => new ProposedLink { Relation = WorkPack.Changed, Target = f.Key }).ToList()
                : [];
            return new ModuleProposal
            {
                Type = WorkPack.ItemType, Key = Clip(id, 200)!,
                Properties = properties.Where(p => p.Value is not null)
                    .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
                Links = links.AsReadOnly(),
                Source = new ProposalSource { Excerpt = ledgerFile, Pack = new PackSource { Id = WorkPack.Id, Version = "", Digest = "", File = ledgerFile, Line = 0 } },
            };
        }).ToList();
        return new ModuleSnapshot { Module = WorkPack.Manifest, Dependencies = [CodeMapPack.Manifest], Source = source, Records = records.AsReadOnly() };
    }

    /// <summary>The ledger ids a commit subject names before its first colon: "CMP-2a, CMP-3 and KP-composite: ...".</summary>
    internal static IReadOnlyList<string> Ids(string subject, IReadOnlySet<string> ids)
    {
        var colon = subject.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return [];
        return [.. subject[..colon].Replace(" and ", ",", StringComparison.Ordinal).Split([',', '+', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(ids.Contains).Distinct(StringComparer.Ordinal)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    private static string? Clip(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
