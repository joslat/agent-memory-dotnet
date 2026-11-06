// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;

namespace AgentMemory.Packs.Work;

/// <summary>One commit as the work pack reads it: its subject and the files it changed.</summary>
[Experimental("AMEXT001")]
public sealed record WorkCommit(string Sha, string Subject, IReadOnlyList<string> Files);

/// <summary>A release as the work pack reads it (CMP-8): its tag, when, its commit, and the commits it shipped since the one before.</summary>
[Experimental("AMEXT001")]
public sealed record WorkRelease(string Name, string? Date, string Commit, IReadOnlyList<string> Shipped);

/// <summary>
/// Reads a task ledger and a repository's commits into a work snapshot (CODING-hierarchy). Every ledger task is one work item;
/// a commit belongs to the items its subject names before the first colon ("CMP-2a, CMP-3: ..."), and each item links to the
/// source files its commits changed, most often changed first, at most <see cref="MaximumFiles"/>.
/// </summary>
/// <remarks>
/// The ledger is a JSON object whose <c>tasks</c> hold <c>id</c>, <c>task</c>, <c>status</c> and optionally <c>phase</c>,
/// <c>notes</c>, <c>remaining</c>, <c>actual</c>, <c>estimate</c>, <c>checkpoint</c>, <c>wave</c> and <c>step</c>; its
/// <c>waves</c> (<c>order</c>, <c>phase</c>) become waves with how many items each holds and how many are done, and its
/// <c>dependencies</c> (<c>before</c>, <c>after</c>) become blocked-by links between items both named (CMP-8). Releases, read
/// from git tags, link each item to the first release that shipped one of its commits. The code map's import writes the files;
/// a link to a file the code map does not hold is left out by the store and counted.
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
        IReadOnlySet<string>? sourceFiles = null, IReadOnlyList<WorkRelease>? releases = null)
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
        // Waves: the ledger's order and phase; an item names its wave by number.
        var waves = new Dictionary<int, string>();
        if (document.RootElement.TryGetProperty("waves", out var waveArray) && waveArray.ValueKind == JsonValueKind.Array)
            foreach (var wave in waveArray.EnumerateArray())
                if (wave.TryGetProperty("order", out var order) && order.TryGetInt32(out var n) && Text(wave, "phase") is { } phase) waves[n] = phase;
        var blocked = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array)
            foreach (var dependency in dependencies.EnumerateArray())
                if (Text(dependency, "before") is { } before && Text(dependency, "after") is { } after && ids.Contains(before) && ids.Contains(after) && before != after)
                    (blocked.TryGetValue(after, out var list) ? list : blocked[after] = []).Add(before);
        var shipped = Shipped(commits, ids, releases);

        var unique = items.DistinctBy(t => Text(t, "id"), StringComparer.Ordinal).ToList();
        var records = unique.Select(task =>
        {
            var id = Text(task, "id")!;
            var wave = Int(task, "wave");
            var properties = new Dictionary<string, object?>
            {
                ["item"] = Clip(id, 64), ["title"] = Clip(Text(task, "task"), 300), ["status"] = Clip(Text(task, "status") ?? "unknown", 32),
                ["phase"] = Clip(Text(task, "phase"), 200), ["summary"] = Clip(Text(task, "notes"), SummaryLength),
                ["remaining"] = Clip(Text(task, "remaining"), 64), ["actual"] = Clip(Text(task, "actual"), 64), ["estimate"] = Clip(Text(task, "estimate"), 64),
                ["checkpoint"] = Clip(Text(task, "checkpoint"), 120), ["commits"] = counts.GetValueOrDefault(id), ["wave"] = wave, ["step"] = Int(task, "step"),
                ["source"] = Clip(ledgerFile + "#" + id, 512),
            };
            var links = files.TryGetValue(id, out var changed)
                ? changed.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(MaximumFiles)
                    .Select(f => new ProposedLink { Relation = WorkPack.Changed, Target = f.Key }).ToList()
                : [];
            if (wave is { } w && waves.ContainsKey(w)) links.Add(new ProposedLink { Relation = WorkPack.InWave, Target = WaveKey(w) });
            foreach (var before in blocked.GetValueOrDefault(id) ?? []) links.Add(new ProposedLink { Relation = WorkPack.BlockedBy, Target = Clip(before, 200)! });
            if (shipped.TryGetValue(id, out var release)) links.Add(new ProposedLink { Relation = WorkPack.ReleasedIn, Target = ReleaseKey(release) });
            return Item(id, properties, links, ledgerFile);
        }).ToList();
        foreach (var (order, phase) in waves.OrderBy(w => w.Key))
        {
            var inWave = unique.Where(t => Int(t, "wave") == order).ToList();
            records.Add(Record(WorkPack.WaveType, WaveKey(order), new()
            {
                ["name"] = Clip(phase, 200), ["order"] = order, ["items"] = inWave.Count,
                ["done"] = inWave.Count(t => string.Equals(Text(t, "status"), "Done", StringComparison.OrdinalIgnoreCase)), ["source"] = Clip($"{ledgerFile}#waves/{order}", 512),
            }, ledgerFile));
        }
        records.AddRange(Releases(releases, shipped));
        return new ModuleSnapshot { Module = WorkPack.Manifest, Dependencies = [CodeMapPack.Manifest], Source = source, Records = records.AsReadOnly() };
    }

    internal static string WaveKey(int order) => "wave:" + order.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string ReleaseKey(WorkRelease release) => "release:" + release.Name;

    // Each item's first release: the earliest that shipped any commit naming it.
    internal static Dictionary<string, WorkRelease> Shipped(IReadOnlyList<WorkCommit> commits, IReadOnlySet<string> ids, IReadOnlyList<WorkRelease>? releases,
        Func<string, IReadOnlySet<string>, IReadOnlyList<string>>? named = null)
    {
        var first = new Dictionary<string, WorkRelease>(StringComparer.Ordinal);
        if (releases is null) return first;
        var subjects = commits.ToDictionary(c => c.Sha, c => c.Subject, StringComparer.Ordinal);
        foreach (var release in releases)
            foreach (var sha in release.Shipped)
                if (subjects.TryGetValue(sha, out var subject))
                    foreach (var id in (named ?? Ids)(subject, ids))
                        first.TryAdd(id, release);
        return first;
    }

    internal static IEnumerable<ModuleProposal> Releases(IReadOnlyList<WorkRelease>? releases, Dictionary<string, WorkRelease> shipped) =>
        (releases ?? []).DistinctBy(r => r.Name, StringComparer.Ordinal).Select(release => Record(WorkPack.ReleaseType, ReleaseKey(release), new()
        {
            ["name"] = Clip(release.Name, 128), ["date"] = Clip(release.Date, 40), ["commit"] = Clip(release.Commit, 64),
            ["items"] = shipped.Count(s => s.Value.Name == release.Name), ["source"] = Clip("git tag " + release.Name, 512),
        }, "git"));

    internal static ModuleProposal Item(string id, Dictionary<string, object?> properties, List<ProposedLink> links, string file) => new()
    {
        Type = WorkPack.ItemType, Key = Clip(id, 200)!,
        Properties = properties.Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
        Links = links.AsReadOnly(),
        Source = new ProposalSource { Excerpt = file, Pack = new PackSource { Id = WorkPack.Id, Version = "", Digest = "", File = file, Line = 0 } },
    };

    internal static ModuleProposal Record(string type, string key, Dictionary<string, object?> properties, string file) => new()
    {
        Type = type, Key = key,
        Properties = properties.Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal).AsReadOnly(),
        Source = new ProposalSource { Excerpt = file },
    };

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    /// <summary>The ledger ids a commit subject names before its first colon: "CMP-2a, CMP-3 and KP-composite: ...".</summary>
    internal static IReadOnlyList<string> Ids(string subject, IReadOnlySet<string> ids)
    {
        var colon = subject.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return [];
        return [.. subject[..colon].Replace(" and ", ",", StringComparison.Ordinal).Split([',', '+', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(ids.Contains).Distinct(StringComparer.Ordinal)];
    }

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    internal static string? Clip(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
