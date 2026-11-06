// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using YamlDotNet.RepresentationModel;

namespace AgentMemory.Packs.Intent;

/// <summary>
/// Reads a repository's product intent into the intent pack's records (CMP-6), deterministically:
/// <list type="bullet">
/// <item>requirements: lines that say "shall" in requirement documents (<c>REQUIREMENTS.md</c>, <c>*requirements*.md</c>,
/// <c>*.req.md</c>, <c>docs/requirements/</c>), each with an id where it starts with one (<c>REQ-12:</c>, <c>[REQ-12]</c>,
/// <c>**REQ-12**</c>), its EARS pattern (When: event; While: state; Where: optional; If ... then: unwanted; several:
/// complex; else ubiquitous), and a status where the line names one (<c>(accepted)</c>, <c>[implemented]</c>);</item>
/// <item>Gherkin <c>.feature</c> files (English keywords): features, rules, scenarios and outlines with their steps; a
/// scenario tagged <c>@REQ-12</c> (or <c>@req:REQ-12</c>) verifies requirement REQ-12;</item>
/// <item>OpenSLO documents (YAML, <c>apiVersion: openslo/…</c>, <c>kind: SLO</c>): objectives with their service, target,
/// window and indicator;</item>
/// <item>ideas: each second-level heading of <c>IDEAS.md</c> or of a file under <c>docs/ideas/</c>, with its body and the
/// status its body names (<c>Status: parked</c>), open by default.</item>
/// </list>
/// </summary>
[Experimental("AMEXT001")]
public static partial class IntentReader
{
    /// <summary>Largest file read.</summary>
    public const int MaximumFileBytes = 4 << 20;
    /// <summary>Most JSON values one YAML file may expand to.</summary>
    public const int MaximumNodes = 100_000;

    /// <summary>The files worth reading, by name.</summary>
    public static IReadOnlyList<string> IntentFiles(IEnumerable<string> repositoryFiles)
    {
        ArgumentNullException.ThrowIfNull(repositoryFiles);
        return [.. repositoryFiles.Select(Normalize).Where(f => Kind(f) is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private static string? Kind(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var folder = path.ToLowerInvariant();
        if (name.EndsWith(".feature", StringComparison.Ordinal)) return "gherkin";
        if (name.EndsWith(".md", StringComparison.Ordinal))
        {
            if (name == "ideas.md" || folder.Contains("docs/ideas/", StringComparison.Ordinal)) return "ideas";
            if (name.Contains("requirements", StringComparison.Ordinal) || name.EndsWith(".req.md", StringComparison.Ordinal) || folder.Contains("docs/requirements/", StringComparison.Ordinal)) return "requirements";
            return null;
        }
        if ((name.EndsWith(".yaml", StringComparison.Ordinal) || name.EndsWith(".yml", StringComparison.Ordinal))
            && (name.Contains("slo", StringComparison.Ordinal) || folder.Contains("openslo", StringComparison.Ordinal))) return "openslo";
        return null;
    }

    /// <summary>Reads the files into one snapshot; <paramref name="skipped"/> names each file left out and why.</summary>
    public static ModuleSnapshot Read(string root, IReadOnlyList<string> files, string source, out IReadOnlyList<string> skipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(files);
        var left = new List<string>();
        var requirements = new List<Record>();
        var features = new List<Record>();
        var scenarios = new List<(Record Scenario, IReadOnlyList<string> Tags, string Feature)>();
        var others = new List<Record>();
        foreach (var file in files.Select(Normalize).Distinct(StringComparer.Ordinal))
        {
            var full = Path.Combine(root, file);
            if (!File.Exists(full)) { left.Add($"{file}: not found"); continue; }
            if (new FileInfo(full).Length > MaximumFileBytes) { left.Add($"{file}: larger than {MaximumFileBytes >> 20} MiB"); continue; }
            var text = File.ReadAllText(full).Replace("\r\n", "\n", StringComparison.Ordinal);
            switch (Kind(file))
            {
                case "requirements": requirements.AddRange(Requirements(file, text)); break;
                case "gherkin":
                    if (Gherkin(file, text, out var feature, out var found) is { } problem) { left.Add($"{file}: {problem}"); break; }
                    features.Add(feature!);
                    scenarios.AddRange(found.Select(s => (s.Scenario, s.Tags, feature!.Key)));
                    break;
                case "openslo":
                    try { others.AddRange(Objectives(file, text)); }
                    catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidDataException) { left.Add($"{file}: {e.Message}"); }
                    break;
                case "ideas": others.AddRange(Ideas(file, text)); break;
            }
        }

        // An id first written wins; a scenario tagged with it verifies it, and the requirement counts its scenarios.
        var byId = requirements.GroupBy(r => (string)r.Properties["reference"]!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var records = new List<ModuleProposal>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scenario, tags, feature) in scenarios)
        {
            var links = new List<ProposedLink> { new() { Relation = IntentPack.ScenarioOf, Target = feature } };
            foreach (var id in tags.Select(RequirementTag).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                if (byId.TryGetValue(id, out var requirement))
                {
                    links.Add(new() { Relation = IntentPack.Verifies, Target = requirement.Key });
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            records.Add(Proposal(scenario, links));
        }
        foreach (var requirement in byId.Values)
        {
            var id = (string)requirement.Properties["reference"]!;
            if (counts.TryGetValue(id, out var count)) requirement.Properties["scenarios"] = count;
            records.Add(Proposal(requirement, []));
        }
        records.AddRange(features.DistinctBy(f => f.Key, StringComparer.Ordinal).Select(f => Proposal(f, [])));
        records.AddRange(others.DistinctBy(o => (o.Type, o.Key)).Select(o => Proposal(o, [])));
        skipped = left;
        var unique = records.GroupBy(r => (r.Type, r.Key)).Select(g => g.First()).OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        return new ModuleSnapshot { Module = IntentPack.Manifest, Source = source, Records = unique.AsReadOnly() };
    }

    internal sealed record Record(string Type, string Key, Dictionary<string, object?> Properties);

    private static ModuleProposal Proposal(Record record, IReadOnlyList<ProposedLink> links) => new()
    {
        Type = record.Type, Key = Clip(record.Key, 600),
        Properties = record.Properties.Where(p => p.Value is not null and not (string { Length: 0 }))
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value is string s ? Clip(s, Limit(record.Type, p.Key)) : p.Value), StringComparer.Ordinal).AsReadOnly(),
        Links = links,
        Source = new ProposalSource { Excerpt = Clip((string)record.Properties["source"]!, 512) },
    };

    private static int Limit(string type, string property) =>
        IntentPack.Manifest.Ontology!.Types.First(t => t.Name == type).Properties.TryGetValue(property, out var p) && p.MaxLength is { } max ? max : 512;

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])?\s*(?:\*\*|\[)?(?<id>[A-Z][A-Z0-9]*-\d+)(?:\*\*|\])?\s*[:.)\-–]?\s+(?<text>.+)$")]
    private static partial Regex IdLine();

    [GeneratedRegex(@"[\(\[](?<status>proposed|accepted|implemented|verified|rejected)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex StatusMark();

    // Lines that say "shall": the EARS form of a requirement, whatever its pattern.
    internal static IEnumerable<Record> Requirements(string file, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!Regex.IsMatch(line, @"\bshall\b", RegexOptions.IgnoreCase) || line.StartsWith('#')) continue;
            var match = IdLine().Match(line);
            var id = match.Success ? match.Groups["id"].Value : $"{file}:{i + 1}";
            var body = (match.Success ? match.Groups["text"].Value : line.TrimStart('-', '*', '+', ' ')).Trim();
            var status = StatusMark().Match(body) is { Success: true } mark ? mark.Groups["status"].Value.ToLowerInvariant() : "proposed";
            body = StatusMark().Replace(body, "").Trim();
            yield return new(IntentPack.RequirementType, id, new()
            {
                ["reference"] = id, ["text"] = body, ["pattern"] = Pattern(body), ["status"] = status, ["file"] = file, ["source"] = $"{file}:{i + 1}",
            });
        }
    }

    // EARS: When (event), While (state), Where (optional feature), If ... then (unwanted behaviour); more than one, complex.
    internal static string Pattern(string text)
    {
        var t = text.TrimStart('*', ' ').ToLowerInvariant();
        var kinds = new List<string>();
        if (t.StartsWith("while ", StringComparison.Ordinal) || t.Contains(", while ", StringComparison.Ordinal)) kinds.Add("state");
        if (t.StartsWith("when ", StringComparison.Ordinal) || t.Contains(", when ", StringComparison.Ordinal) || t.Contains(" when ", StringComparison.Ordinal) && kinds.Count > 0) kinds.Add("event");
        if (t.StartsWith("where ", StringComparison.Ordinal)) kinds.Add("optional");
        if (t.StartsWith("if ", StringComparison.Ordinal) && t.Contains(" then ", StringComparison.Ordinal)) kinds.Add("unwanted");
        return kinds.Count switch
        {
            0 => t.Contains(" shall ", StringComparison.Ordinal) && t.StartsWith("the ", StringComparison.Ordinal) ? "ubiquitous" : "free",
            1 => kinds[0],
            _ => "complex",
        };
    }

    // A requirement id from a scenario tag: @REQ-12, or @req:REQ-12.
    private static string? RequirementTag(string tag)
    {
        var value = tag.TrimStart('@');
        if (value.StartsWith("req:", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return Regex.IsMatch(value, @"^[A-Za-z][A-Za-z0-9]*-\d+$") ? value : null;
    }

    private static readonly string[] StepKeywords = ["Given ", "When ", "Then ", "And ", "But ", "* "];

    // A Gherkin file, English keywords: one feature, its rules, its scenarios and outlines, each with its steps and tags.
    internal static string? Gherkin(string file, string text, out Record? feature, out List<(Record Scenario, IReadOnlyList<string> Tags)> scenarios)
    {
        feature = null;
        var found = new List<(Record Scenario, IReadOnlyList<string> Tags)>();
        scenarios = found;
        var lines = text.Split('\n');
        if (lines.FirstOrDefault(l => l.TrimStart().StartsWith("# language:", StringComparison.Ordinal)) is { } language && !language.Contains(": en", StringComparison.Ordinal))
            return "only English Gherkin keywords are read";
        var pending = new List<string>();
        var featureTags = new List<string>();
        string? rule = null;
        (string Name, int Line, bool Outline, List<string> Tags, StringBuilder Steps)? current = null;
        var description = new StringBuilder();
        var describing = false; // the feature's own text: from its line to its first background, rule or scenario
        var inDocString = false;
        void Close()
        {
            if (current is not { } s) return;
            var key = $"{file}:{s.Line}";
            found.Add((new Record(IntentPack.ScenarioType, key, new()
            {
                ["name"] = s.Name, ["steps"] = s.Steps.ToString().Trim(), ["tags"] = string.Join(' ', s.Tags), ["outline"] = s.Outline ? "yes" : null,
                ["rule"] = rule, ["file"] = file, ["line"] = s.Line, ["source"] = key,
            }), s.Tags));
            current = null;
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("\"\"\"", StringComparison.Ordinal) || line.StartsWith("```", StringComparison.Ordinal)) { inDocString = !inDocString; continue; }
            if (inDocString || line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('@')) { pending.AddRange(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.StartsWith('@'))); continue; }
            if (line.StartsWith("Feature:", StringComparison.Ordinal))
            {
                feature = new Record(IntentPack.FeatureType, file, new() { ["name"] = line[8..].Trim(), ["file"] = file, ["source"] = $"{file}:{i + 1}" });
                featureTags.AddRange(pending);
                pending.Clear();
                describing = true;
                continue;
            }
            if (line.StartsWith("Rule:", StringComparison.Ordinal)) { Close(); describing = false; rule = line[5..].Trim(); pending.Clear(); continue; }
            if (line.StartsWith("Background:", StringComparison.Ordinal)) { Close(); describing = false; pending.Clear(); continue; }
            var outline = line.StartsWith("Scenario Outline:", StringComparison.Ordinal) || line.StartsWith("Scenario Template:", StringComparison.Ordinal);
            if (outline || line.StartsWith("Scenario:", StringComparison.Ordinal) || line.StartsWith("Example:", StringComparison.Ordinal))
            {
                Close();
                describing = false;
                current = (line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim(), i + 1, outline, [.. featureTags, .. pending], new StringBuilder());
                pending.Clear();
                continue;
            }
            if (line.StartsWith("Examples:", StringComparison.Ordinal) || line.StartsWith("Scenarios:", StringComparison.Ordinal) || line.StartsWith('|')) continue;
            if (current is { } open && StepKeywords.Any(k => line.StartsWith(k, StringComparison.Ordinal))) { open.Steps.Append(line).Append('\n'); continue; }
            if (describing) description.Append(line).Append(' ');
        }
        Close();
        if (feature is null) return "no Feature: line";
        feature.Properties["description"] = description.ToString().Trim();
        feature.Properties["tags"] = string.Join(' ', featureTags);
        return null;
    }

    // OpenSLO SLO documents.
    internal static IEnumerable<Record> Objectives(string file, string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var budget = MaximumNodes;
        foreach (var document in stream.Documents)
        {
            if (document.RootNode is not YamlMappingNode map) continue;
            Count(map, ref budget);
            if (Scalar(map, "apiVersion") is not { } api || !api.StartsWith("openslo/", StringComparison.Ordinal) || Scalar(map, "kind") != "SLO") continue;
            var metadata = Child(map, "metadata");
            var spec = Child(map, "spec");
            if (Scalar(metadata, "name") is not { } name) continue;
            var objective = (Child(spec, "objectives") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().FirstOrDefault();
            var target = Scalar(objective, "target") ?? (Scalar(objective, "targetPercent") is { } percent ? percent + "%" : null);
            var window = (Child(spec, "timeWindow") as YamlSequenceNode)?.Children.OfType<YamlMappingNode>().Select(w => Scalar(w, "duration")).FirstOrDefault();
            var indicator = Scalar(spec, "indicatorRef") ?? Scalar(Child(Child(spec, "indicator"), "metadata"), "name");
            yield return new(IntentPack.ObjectiveType, $"{file}#{name}", new()
            {
                ["name"] = Scalar(metadata, "displayName") ?? name, ["service"] = Scalar(spec, "service"),
                ["target"] = target, ["window"] = window, ["indicator"] = indicator, ["description"] = Scalar(spec, "description"), ["file"] = file,
                ["source"] = $"{file}#{name}",
            });
        }
    }

    // Aliases can make a small YAML file enormous: count what it expands to, within a budget.
    private static void Count(YamlNode node, ref int budget)
    {
        if (--budget < 0) throw new InvalidDataException($"it expands to more than {MaximumNodes} values");
        switch (node)
        {
            case YamlMappingNode map: foreach (var (key, value) in map.Children) { Count(key, ref budget); Count(value, ref budget); } break;
            case YamlSequenceNode sequence: foreach (var item in sequence.Children) Count(item, ref budget); break;
        }
    }

    private static YamlNode? Child(YamlNode? node, string name) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(name), out var value) ? value : null;

    private static string? Scalar(YamlNode? node, string name) => Child(node, name) is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    // Each second-level heading of an ideas file is an idea; its body until the next heading; "Status: x" in it names its status.
    internal static IEnumerable<Record> Ideas(string file, string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("## ", StringComparison.Ordinal)) continue;
            var title = lines[i][3..].Trim();
            var body = new StringBuilder();
            var status = "open";
            for (var j = i + 1; j < lines.Length && !lines[j].StartsWith("#", StringComparison.Ordinal); j++)
            {
                var line = lines[j].Trim();
                if (Regex.Match(line, @"^\**status\**:\s*\**(?<s>[A-Za-z-]+)", RegexOptions.IgnoreCase) is { Success: true } m) { status = m.Groups["s"].Value.ToLowerInvariant(); continue; }
                if (line.Length > 0) body.Append(line).Append(' ');
            }
            yield return new(IntentPack.IdeaType, $"{file}:{i + 1}", new()
            {
                ["title"] = title, ["body"] = body.ToString().Trim(), ["status"] = status, ["file"] = file, ["line"] = i + 1, ["source"] = $"{file}:{i + 1}",
            });
        }
    }

    internal static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Trim().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." && segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..(length - 1)].TrimEnd() + "…";
}
