// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.Intent;

namespace AgentMemory.Packs.Work;

/// <summary>One commit as the work pack reads it: its subject and the files it changed.</summary>
[Experimental("AMEXT001")]
public sealed record WorkCommit(string Sha, string Subject, IReadOnlyList<string> Files);

/// <summary>A release as the work pack reads it (CMP-8): its tag, when, its commit, and the commits it shipped since the one before.</summary>
[Experimental("AMEXT001")]
public sealed record WorkRelease(string Name, string? Date, string Commit, IReadOnlyList<string> Shipped);

/// <summary>Where a commit subject names the ledger ids it belongs to.</summary>
[Experimental("AMEXT001")]
public enum WorkCommitIds
{
    /// <summary>Before its first colon: "CMP-2a, CMP-3: ...". The default.</summary>
    BeforeColon = 0,
    /// <summary>Anywhere, as a whole token: "... (CMP-2a)" as well (CO-2d; this repository's later commits name rows so).</summary>
    Anywhere = 1,
}

/// <summary>What a ledger read takes besides the ledger and the commits (CO-2b); every member is optional.</summary>
[Experimental("AMEXT001")]
public sealed record WorkLedgerOptions
{
    /// <summary>When given, links only to these source paths are kept.</summary>
    public IReadOnlySet<string>? SourceFiles { get; init; }
    /// <summary>The releases, from git tags.</summary>
    public IReadOnlyList<WorkRelease>? Releases { get; init; }
    /// <summary>
    /// The ids of the requirements an item may implement (CO-2b, <see cref="WorkPack.Implements"/>): the intent pack's, or, for a
    /// ledger read as intent, the ledger's own row ids. Null: no item implements anything.
    /// </summary>
    public IReadOnlySet<string>? Requirements { get; init; }
    /// <summary>Where a commit subject names its ledger ids.</summary>
    public WorkCommitIds CommitIds { get; init; } = WorkCommitIds.BeforeColon;
}

/// <summary>
/// Reads a task ledger and a repository's commits into a work snapshot (CODING-hierarchy). Every ledger task is one work item;
/// a commit belongs to the items its subject names before the first colon ("CMP-2a, CMP-3: ...", or anywhere with
/// <see cref="WorkCommitIds.Anywhere"/>), and each item links to the
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
public static partial class WorkLedgerReader
{
    /// <summary>Most files one work item links to.</summary>
    public const int MaximumFiles = 64;
    /// <summary>How much of an item's notes it keeps as its summary.</summary>
    public const int SummaryLength = 1200;

    /// <summary>Most requirements one work item links to as implementing them (CO-2b).</summary>
    public const int MaximumImplemented = 16;

    /// <summary>
    /// Builds the snapshot with <paramref name="options"/> (CO-2b): which requirements an item may implement, and where a commit
    /// names its ids. Null options read as the plain overload does. An item implements, among
    /// <see cref="WorkLedgerOptions.Requirements"/>: its own id (a ledger read as intent), the ids its title names, and those its
    /// <c>implements</c> field lists (a string or an array of strings); its notes name related rows as context, not claims.
    /// </summary>
    public static ModuleSnapshot Read(string ledgerJson, string ledgerFile, IReadOnlyList<WorkCommit> commits, string source, WorkLedgerOptions? options) =>
        Build(ledgerJson, ledgerFile, commits, source, options ?? new WorkLedgerOptions());

    /// <summary>Builds the snapshot. <paramref name="sourceFiles"/>, when given, keeps only links to those paths.</summary>
    public static ModuleSnapshot Read(string ledgerJson, string ledgerFile, IReadOnlyList<WorkCommit> commits, string source,
        IReadOnlySet<string>? sourceFiles = null, IReadOnlyList<WorkRelease>? releases = null) =>
        Build(ledgerJson, ledgerFile, commits, source, new WorkLedgerOptions { SourceFiles = sourceFiles, Releases = releases });

    private static ModuleSnapshot Build(string ledgerJson, string ledgerFile, IReadOnlyList<WorkCommit> commits, string source, WorkLedgerOptions options)
    {
        var sourceFiles = options.SourceFiles;
        var releases = options.Releases;
        var requirements = options.Requirements;
        Func<string, IReadOnlySet<string>, IReadOnlyList<string>> named = options.CommitIds == WorkCommitIds.Anywhere ? IdsAnywhere : Ids;
        ArgumentNullException.ThrowIfNull(ledgerJson); ArgumentNullException.ThrowIfNull(commits);
        using var document = JsonDocument.Parse(ledgerJson);
        if (!document.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
            throw new FormatException("A task ledger is a JSON object with a tasks array.");
        var items = tasks.EnumerateArray().Where(t => Text(t, "id") is { Length: > 0 } && Text(t, "task") is { Length: > 0 }).ToList();
        var ids = items.Select(t => Text(t, "id")!).ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commit in commits)
            foreach (var id in named(commit.Subject, ids))
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
        var shipped = Shipped(commits, ids, releases, named);

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
            if (requirements is not null)
                links.AddRange(Implemented(id, task, requirements).Select(r => new ProposedLink { Relation = WorkPack.Implements, Target = Clip(r, 600)! }));
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
        return new ModuleSnapshot { Module = WorkPack.Manifest, Dependencies = Dependencies, Source = source, Records = records.AsReadOnly() };
    }

    /// <summary>The manifests a work snapshot names: the code map its items changed, and the intent its items implement.</summary>
    internal static IReadOnlyList<Extensibility.Modules.ModuleManifest> Dependencies => [CodeMapPack.Manifest, IntentPack.Manifest];

    // What an item implements (CO-2b), among the requirements given: its own row, then what its title names, then its implements field.
    private static IEnumerable<string> Implemented(string id, JsonElement task, IReadOnlySet<string> requirements)
    {
        IEnumerable<string> named = [id, .. Tokens(Text(task, "task") ?? "")];
        if (task.TryGetProperty("implements", out var implements))
            named = named.Concat(implements.ValueKind switch
            {
                JsonValueKind.String => Tokens(implements.GetString()!),
                JsonValueKind.Array => implements.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).SelectMany(e => Tokens(e.GetString()!)),
                _ => [],
            });
        return named.Where(requirements.Contains).Distinct(StringComparer.Ordinal).Take(MaximumImplemented);
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

    /// <summary>The ledger ids a commit subject names anywhere as a whole token (CO-2d): "... (PACK-REMOVE, D-06)".</summary>
    internal static IReadOnlyList<string> IdsAnywhere(string subject, IReadOnlySet<string> ids) =>
        [.. Tokens(subject).Where(ids.Contains).Distinct(StringComparer.Ordinal)];

    // Whole tokens: letters, digits and '_', joined by '-' or '.' only between them ("CMP-2a", "40.28", "KP-composite"), so
    // "CMP-2a's" holds CMP-2a, while "XCMP-3" and "CMP-3-extra" do not hold CMP-3.
    private static IEnumerable<string> Tokens(string text) => Token().Matches(text).Select(m => m.Value);

    [System.Text.RegularExpressions.GeneratedRegex(@"[A-Za-z0-9_]+(?:[-.][A-Za-z0-9_]+)*", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Token();

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    internal static string? Clip(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
