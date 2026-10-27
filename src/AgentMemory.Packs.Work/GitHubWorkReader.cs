using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.CodeMap;

namespace AgentMemory.Packs.Work;

/// <summary>
/// Reads GitHub issues and milestones into a work snapshot (CMP-8), offline, from what <c>gh</c> exports:
/// <c>gh issue list --state all --json number,title,state,milestone,labels,body,url --limit 1000</c> and
/// <c>gh api "repos/{owner}/{repo}/milestones?state=all"</c>. Each issue is a work item <c>#123</c> (Done when closed), in
/// the wave of its milestone; a commit whose subject names <c>#123</c> anywhere counts for it, and links it to the files the
/// commit changed and to the first release that shipped it.
/// </summary>
[Experimental("AMEXT001")]
public static partial class GitHubWorkReader
{
    /// <summary>Builds the snapshot from the two exports (milestones optional).</summary>
    public static ModuleSnapshot Read(string issuesJson, string? milestonesJson, IReadOnlyList<WorkCommit> commits, string source,
        IReadOnlySet<string>? sourceFiles = null, IReadOnlyList<WorkRelease>? releases = null)
    {
        ArgumentNullException.ThrowIfNull(issuesJson);
        ArgumentNullException.ThrowIfNull(commits);
        using var issuesDocument = JsonDocument.Parse(issuesJson);
        if (issuesDocument.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("gh issue list --json writes an array of issues.");
        var issues = issuesDocument.RootElement.EnumerateArray().Where(i => i.TryGetProperty("number", out var n) && n.TryGetInt32(out _)).ToList();
        var ids = issues.Select(i => "#" + i.GetProperty("number").GetInt32()).ToHashSet(StringComparer.Ordinal);

        // Milestones as waves, in the order they fall due.
        var milestones = new List<(int Number, string Title, string? State, string? Due)>();
        if (milestonesJson is not null)
        {
            using var document = JsonDocument.Parse(milestonesJson);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var milestone in document.RootElement.EnumerateArray())
                    if (milestone.TryGetProperty("number", out var n) && n.TryGetInt32(out var number) && WorkLedgerReader.Text(milestone, "title") is { } title)
                        milestones.Add((number, title, WorkLedgerReader.Text(milestone, "state"), WorkLedgerReader.Text(milestone, "due_on") ?? WorkLedgerReader.Text(milestone, "dueOn")));
        }
        foreach (var issue in issues)
            if (issue.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("number", out var mn) && mn.TryGetInt32(out var number)
                && milestones.All(x => x.Number != number) && WorkLedgerReader.Text(m, "title") is { } title)
                milestones.Add((number, title, WorkLedgerReader.Text(m, "state"), WorkLedgerReader.Text(m, "dueOn") ?? WorkLedgerReader.Text(m, "due_on")));
        var ordered = milestones.OrderBy(m => m.Due is null).ThenBy(m => m.Due, StringComparer.Ordinal).ThenBy(m => m.Number).ToList();

        var files = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commit in commits)
            foreach (var id in Named(commit.Subject, ids))
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                var changed = files.TryGetValue(id, out var known) ? known : files[id] = new(StringComparer.Ordinal);
                foreach (var file in commit.Files.Select(WorkLedgerReader.Normalize).Where(f => sourceFiles is null || sourceFiles.Contains(f)))
                    changed[file] = changed.GetValueOrDefault(file) + 1;
            }
        var shipped = WorkLedgerReader.Shipped(commits, ids, releases, Named);

        var records = new List<ModuleProposal>();
        var waveOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            var id = "#" + issue.GetProperty("number").GetInt32();
            var closed = string.Equals(WorkLedgerReader.Text(issue, "state"), "closed", StringComparison.OrdinalIgnoreCase);
            var milestone = issue.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("number", out var mn) && mn.TryGetInt32(out var number) ? number : (int?)null;
            if (milestone is { } owned) waveOf[id] = owned;
            var labels = issue.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? string.Join(", ", l.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? WorkLedgerReader.Text(x, "name") : x.ValueKind == JsonValueKind.String ? x.GetString() : null).OfType<string>())
                : "";
            var properties = new Dictionary<string, object?>
            {
                ["item"] = id, ["title"] = WorkLedgerReader.Clip(WorkLedgerReader.Text(issue, "title"), 300) ?? id, ["status"] = closed ? "Done" : "Open",
                ["phase"] = WorkLedgerReader.Clip(milestone is { } wave ? milestones.First(x => x.Number == wave).Title : null, 200),
                ["summary"] = WorkLedgerReader.Clip(WorkLedgerReader.Text(issue, "body"), WorkLedgerReader.SummaryLength), ["labels"] = labels.Length == 0 ? null : WorkLedgerReader.Clip(labels, 500),
                ["url"] = WorkLedgerReader.Clip(WorkLedgerReader.Text(issue, "url"), 512), ["commits"] = counts.GetValueOrDefault(id),
                ["wave"] = milestone is { } w ? ordered.FindIndex(x => x.Number == w) + 1 : null, ["source"] = "github " + id,
            };
            var links = files.TryGetValue(id, out var changed)
                ? changed.OrderByDescending(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(WorkLedgerReader.MaximumFiles)
                    .Select(f => new ProposedLink { Relation = WorkPack.Changed, Target = f.Key }).ToList()
                : [];
            if (milestone is { } inWave) links.Add(new ProposedLink { Relation = WorkPack.InWave, Target = "milestone:" + inWave });
            if (shipped.TryGetValue(id, out var release)) links.Add(new ProposedLink { Relation = WorkPack.ReleasedIn, Target = WorkLedgerReader.ReleaseKey(release) });
            records.Add(WorkLedgerReader.Item(id, properties, links, "github"));
        }
        for (var i = 0; i < ordered.Count; i++)
        {
            var (number, title, state, due) = ordered[i];
            var inWave = waveOf.Where(w => w.Value == number).Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            records.Add(WorkLedgerReader.Record(WorkPack.WaveType, "milestone:" + number, new()
            {
                ["name"] = WorkLedgerReader.Clip(title, 200), ["order"] = i + 1, ["items"] = inWave.Count,
                ["done"] = issues.Count(x => inWave.Contains("#" + x.GetProperty("number").GetInt32()) && string.Equals(WorkLedgerReader.Text(x, "state"), "closed", StringComparison.OrdinalIgnoreCase)),
                ["due"] = WorkLedgerReader.Clip(due, 40), ["state"] = WorkLedgerReader.Clip(state?.ToLowerInvariant(), 32), ["source"] = "github milestone " + number,
            }, "github"));
        }
        records.AddRange(WorkLedgerReader.Releases(releases, shipped));
        return new ModuleSnapshot { Module = WorkPack.Manifest, Dependencies = [CodeMapPack.Manifest], Source = source, Records = records.AsReadOnly() };
    }

    [GeneratedRegex(@"#(?<n>\d+)\b")]
    private static partial Regex IssueReference();

    // The issues a commit subject names, anywhere: "Fix the map (#12)", "Closes #7".
    internal static IReadOnlyList<string> Named(string subject, IReadOnlySet<string> ids) =>
        [.. IssueReference().Matches(subject).Select(m => "#" + m.Groups["n"].Value).Where(ids.Contains).Distinct(StringComparer.Ordinal)];
}
