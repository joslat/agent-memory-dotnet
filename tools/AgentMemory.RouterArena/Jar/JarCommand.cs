using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.RouterArena.Jar;

/// <summary>
/// <c>router-arena jar cv|freeze|read|replay --gen &lt;generation folder&gt;</c> (root PLAN 40.98): the jar's selection in
/// the arena. <c>cv</c> estimates every family by five-fold cross-validation on the training turns (v2 + v3); <c>freeze</c>
/// chooses each family's setting on all of them and writes the frozen forms; <c>read</c> scores the frozen forms on a test
/// set once and refuses a second read; <c>replay</c> rescores a set that was already read and compares with the recorded
/// read (a reproduction, never a test). Families: <c>--gates a,b</c>, <c>--blends local+online,…</c>,
/// <c>--hybrids local+online,…</c>, <c>--modules a</c>, <c>--norouter</c>; <c>--labels judged|first</c>.
/// </summary>
public static class JarCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(string[] args, Func<string, string?> get, string matrices, TextWriter output)
    {
        if (args.Length < 2 || get("gen") is not { } genArg)
        {
            output.WriteLine("router-arena jar cv|freeze|read|replay --gen <generation folder> [--set v4|w2] [--gates …] [--blends …] [--hybrids …] [--modules …] [--norouter] [--labels judged|first] [--compare <file>]");
            return 1;
        }
        var gen = Path.GetFullPath(genArg);
        var judged = (get("labels") ?? "judged") == "judged";
        JarSet Set(string name) => name switch
        {
            "v2" => JarSet.Read("v2", gen, Path.Combine(matrices, judged ? "routing-matrix-v2.1.json" : "routing-matrix-v2.json"), atRoot: true),
            "v3" => JarSet.Read("v3", Path.Combine(gen, "fresh"), Path.Combine(matrices, judged ? "routing-matrix-v3.1-fresh.json" : "routing-matrix-v3-fresh.json"), atRoot: false),
            "v4" => JarSet.Read("v4", Path.Combine(gen, "fresh-v4"), Path.Combine(matrices, judged ? "routing-matrix-v4.1-fresh.json" : "routing-matrix-v4-fresh.json"), atRoot: false),
            "w2" => JarSet.Read("w2", Path.Combine(gen, "world2"),
                get("w2-items") ?? Path.Combine(Path.GetDirectoryName(matrices)!, "world2", "matrix-w2-items.json"), atRoot: false),
            _ => throw new ArgumentException($"unknown set '{name}': v2, v3, v4 or w2"),
        };

        switch (args[1])
        {
            case "cv" or "freeze":
            {
                var train = JarTurn.Of(Set("v2"), Set("v3"));
                var freeze = args[1] == "freeze";
                var results = new JsonObject();
                var forms = new JsonObject();
                foreach (var (budgetName, budget) in JarScore.Budgets)
                {
                    foreach (var (name, candidates) in Families(get, args.Contains("--norouter")))
                    {
                        var limit = name == "today" ? 1e9 : budget;
                        var key = $"{name}@{budgetName}";
                        if (freeze)
                        {
                            if (JarScore.Choose(candidates, train, limit) is { } chosen)
                                forms[key] = JsonSerializer.SerializeToNode(chosen, FormJson);
                            continue;
                        }
                        if (JarScore.CrossValidate(candidates, train, limit) is not { } estimate)
                            continue;
                        var r = estimate.Result;
                        output.WriteLine(Invariant($"{budgetName,-9} {name,-44} CV {r.Served,3}/{r.Servable} ({r.ServedPct}%)  clutter {r.ClutterPerTurn} a turn ({r.ClutterPct}%)"));
                        results[key] = JsonSerializer.SerializeToNode(r);
                    }
                }
                if (!freeze)
                {
                    File.WriteAllText(Path.Combine(gen, "cv-results.net.json"), results.ToJsonString(Indented));
                    return 0;
                }
                var path = Path.Combine(gen, "final-forms.net.json");
                File.WriteAllText(path, new JsonObject
                {
                    ["rule"] = "most served within the clutter budget, ties to less clutter; chosen on all the training turns",
                    ["forms"] = forms,
                }.ToJsonString(Indented));
                output.WriteLine($"frozen {forms.Count} forms to {path}");
                return get("compare") is { } compare ? CompareForms(forms, compare, output) : 0;
            }
            case "read" or "replay":
            {
                var setName = get("set") ?? "v4";
                var marker = Path.Combine(gen, $"{setName}-read.json");
                var replay = args[1] == "replay";
                if (!replay && File.Exists(marker))
                {
                    output.WriteLine($"{setName} was read already ({marker}); a second read is not a test");
                    return 1;
                }
                var frozenPath = get("forms") ?? Path.Combine(gen, "final-forms.json");
                using var frozen = JsonDocument.Parse(File.ReadAllBytes(frozenPath));
                var test = JarTurn.Of(Set(setName));
                var results = new JsonObject();
                foreach (var entry in frozen.RootElement.GetProperty("forms").EnumerateObject())
                {
                    var form = entry.Value.Deserialize<JarForm>(FormJson)!;
                    var s = JarScore.Score(form, test);
                    var (wins, losses) = JarScore.AgainstToday(form, test);
                    var row = JsonSerializer.SerializeToNode(s)!.AsObject();
                    row["vsToday"] = $"+{wins} -{losses}";
                    row["p"] = Math.Round(JarScore.SignTest(wins, losses), 4);
                    row["form"] = JsonSerializer.SerializeToNode(form, FormJson);
                    results[entry.Name] = row;
                    output.WriteLine(Invariant($"{entry.Name,-28} {s.Served,3}/{s.Servable} ({s.ServedPct}%)  clutter {s.ClutterPerTurn} a turn ({s.ClutterPct}%)  {s.MsP50} ms  vs today +{wins} -{losses}"));
                }
                if (replay)
                    return CompareReads(results, get("compare") ?? marker, output);
                var sha = frozen.RootElement.TryGetProperty("sha", out var s0) ? s0.GetString() : null;
                File.WriteAllText(marker, new JsonObject { ["frozen"] = sha, ["results"] = results }.ToJsonString(Indented));
                return 0;
            }
            default:
                output.WriteLine($"router-arena jar: unknown step '{args[1]}' (cv, freeze, read, replay)");
                return 1;
        }
    }

    /// <summary>The settings a frozen form is written with: only those its family uses.</summary>
    public static readonly JsonSerializerOptions FormJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    // The families asked for, each named as the frozen forms name them ("gate:<judge>", "blend:<local>+<online>", …).
    private static IEnumerable<(string Name, IReadOnlyList<JarForm> Candidates)> Families(Func<string, string?> get, bool noRouter)
    {
        IEnumerable<string> List(string name) => (get(name) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        yield return ("today", [.. JarScore.Candidates("today")]);
        foreach (var judge in List("gates"))
            yield return ($"gate:{judge}", [.. JarScore.Candidates("gate", judge)]);
        foreach (var pair in List("hybrids"))
            yield return ($"hybrid:{pair}", [.. JarScore.Candidates("hybrid", local: pair.Split('+')[0], online: pair.Split('+')[1])]);
        foreach (var pair in List("blends"))
            yield return ($"blend:{pair}", [.. JarScore.Candidates("blend", local: pair.Split('+')[0], online: pair.Split('+')[1])]);
        foreach (var judge in List("modules"))
            yield return ($"module:{judge}", [.. JarScore.Candidates("module", judge)]);
        if (noRouter)
            foreach (var family in new[] { "cospool", "cosfloor", "cosdoor" })
                yield return (family, [.. JarScore.Candidates(family)]);
    }

    // The forms this freeze chose against another freeze (the twin written by the first implementation): same keys, same settings.
    private static int CompareForms(JsonObject forms, string comparePath, TextWriter output)
    {
        using var other = JsonDocument.Parse(File.ReadAllBytes(comparePath));
        var theirs = other.RootElement.GetProperty("forms").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Deserialize<JarForm>(FormJson)!);
        var differ = 0;
        foreach (var (key, node) in forms)
        {
            var mine = node.Deserialize<JarForm>(FormJson)!;
            if (!theirs.TryGetValue(key, out var form) || form != mine)
            {
                differ++;
                output.WriteLine($"differs: {key}: {node!.ToJsonString()} against {(form is null ? "nothing" : JsonSerializer.Serialize(form, FormJson))}");
            }
        }
        foreach (var key in theirs.Keys.Where(k => !forms.ContainsKey(k)))
        {
            differ++;
            output.WriteLine($"differs: {key} is only in {comparePath}");
        }
        output.WriteLine(differ == 0 ? $"the same {forms.Count} forms as {comparePath}" : $"{differ} forms differ from {comparePath}");
        return differ == 0 ? 0 : 1;
    }

    // A rescored read against the recorded one: every count equal, every rounded share within its rounding.
    private static int CompareReads(JsonObject results, string comparePath, TextWriter output)
    {
        using var recorded = JsonDocument.Parse(File.ReadAllBytes(comparePath));
        var theirs = recorded.RootElement.GetProperty("results");
        var differ = 0;
        foreach (var (key, node) in results)
        {
            if (!theirs.TryGetProperty(key, out var their))
            {
                differ++;
                output.WriteLine($"differs: {key} is not in {comparePath}");
                continue;
            }
            bool Near(string field, double tolerance) => Math.Abs(node![field]!.GetValue<double>() - their.GetProperty(field).GetDouble()) <= tolerance;
            var same = node!["served"]!.GetValue<int>() == their.GetProperty("served").GetInt32()
                && node["servable"]!.GetValue<int>() == their.GetProperty("servable").GetInt32()
                && Near("clutterPerTurn", 0.011) && Near("clutterPct", 0.11) && Near("msP50", 1.01)
                && node["vsToday"]!.GetValue<string>() == their.GetProperty("vsToday").GetString();
            if (!same)
            {
                differ++;
                output.WriteLine($"differs: {key}: {node.ToJsonString()} against {their.GetRawText()}");
            }
        }
        output.WriteLine(differ == 0 ? $"replayed {results.Count} forms: every one the same as {comparePath}" : $"{differ} of {results.Count} forms differ from {comparePath}");
        return differ == 0 ? 0 : 1;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
