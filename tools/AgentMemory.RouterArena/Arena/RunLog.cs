using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.RouterArena.Arena;

/// <summary>
/// Every fight is logged: a run folder (<c>runs/&lt;time&gt;__&lt;round&gt;/run.json</c>: what was fought, on which inputs, by which
/// code, every turn's score) and one line per contestant appended to <c>leaderboard.jsonl</c>, from which
/// <c>leaderboard.md</c> is rendered: the best run of every method, per split.
/// </summary>
public static class RunLog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions Line = new(Json) { WriteIndented = false };

    public static string Write(string home, string round, string split, ArenaData data, IReadOnlyDictionary<string, ContestantScore> results,
        string notes, IReadOnlyDictionary<string, object>? extra = null)
    {
        var at = DateTimeOffset.UtcNow;
        var folder = Path.Combine(home, "runs", $"{at:yyyyMMdd-HHmmss}__{round}");
        Directory.CreateDirectory(folder);
        var fingerprint = new Dictionary<string, object?>
        {
            ["round"] = round, ["split"] = split, ["at"] = at.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["referee"] = "router-arena (.NET)", ["libraryCommit"] = Commit(), ["arenaBuild"] = typeof(RunLog).Assembly.ManifestModule.ModuleVersionId.ToString(),
            ["inputs"] = data.InputShas,
        };
        File.WriteAllText(Path.Combine(folder, "run.json"),
            JsonSerializer.Serialize(new { fingerprint, notes, extra, results }, Json));
        var board = Path.Combine(home, "leaderboard.jsonl");
        var lines = new StringBuilder();
        foreach (var (id, r) in results)
            lines.AppendLine(JsonSerializer.Serialize(new
            {
                run = Path.GetFileName(folder), round, split, at = fingerprint["at"], contestant = id, family = r.Family, @params = r.Params,
                r.Turns, r.Needing, r.Served, r.ServedShare, r.MissedGroups, r.RoutingMisses, r.RetrievalMisses, r.ServedIfRetrievalPerfect,
                r.DoorsLabelled, r.DoorRecall, r.DoorPrecision, r.DoorsExact, r.DoorsPerTurn,
                r.NoisePerTurn, r.SizePerTurn, r.SearchesPerTurn, r.ModelCallsPerTurn, r.ModelSecondsPerTurn,
                r.Quiet, r.NothingNeeded, r.Breaches, referee = ".NET", matrix = data.InputShas["matrix"][..12], recording = data.InputShas["recording"][..12],
            }, Line));
        File.AppendAllText(board, lines.ToString());
        RenderLeaderboard(home);
        return folder;
    }

    /// <summary>The best run of every method per matrix and split, ranked by served, then noise, then cost.</summary>
    public static void RenderLeaderboard(string home)
    {
        var board = Path.Combine(home, "leaderboard.jsonl");
        if (!File.Exists(board)) return;
        var rows = File.ReadAllLines(board).Where(l => l.Trim().Length > 0).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var md = new StringBuilder();
        md.AppendLine("# Router arena leaderboard").AppendLine()
          .AppendLine("Rendered from `leaderboard.jsonl` (every run of every method, by both referees: the Python prototype and the .NET arena). "
                      + "Best run per method, matrix and split; ranked by served, then noise, then cost. Run logs: `runs/`. "
                      + "Doors: needed doors opened (recall) / opened doors needed or accepted (precision); on a matrix without door labels, "
                      + "against the doors that hold what the turn needs.").AppendLine();
        string MatrixOf(JsonElement r) => r.TryGetProperty("matrix", out var m) ? m.GetString() ?? "?" : "?";
        foreach (var (matrix, split) in rows.Select(r => (MatrixOf(r), r.GetProperty("split").GetString()!)).Distinct()
                     .OrderByDescending(s => rows.Last(r => MatrixOf(r) == s.Item1).GetProperty("at").GetString(), StringComparer.Ordinal)
                     .ThenBy(s => s.Item2, StringComparer.Ordinal))
        {
            var mine = rows.Where(r => MatrixOf(r) == matrix && r.GetProperty("split").GetString() == split).ToList();
            var best = mine.GroupBy(r => r.GetProperty("contestant").GetString()!)
                .Select(g => (Runs: g.Count(), Best: g.OrderBy(Key).First()))
                .OrderBy(b => Key(b.Best)).ToList();
            md.AppendLine($"## Matrix {matrix} · {(split == "dev" ? "tuning turns (dev)" : "held-out turns (the final)")}").AppendLine()
              .AppendLine("| # | Method | Family | Served | Routing misses | Search misses | Doors R / P | Doors exact | Noise / turn | Searches / turn | Model calls / turn | Quiet on small talk | Breaches | Runs | Best run |")
              .AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            var n = 0;
            foreach (var (runs, r) in best)
            {
                string S(string p) => r.TryGetProperty(p, out var v) ? v.ToString() : "–";
                md.AppendLine(CultureInfo.InvariantCulture, $"| {++n} | {S("contestant")} | {S("family")} | {S("served")}/{S("needing")} | {S("routingMisses")} | {S("retrievalMisses")} | {S("doorRecall")} / {S("doorPrecision")} | {S("doorsExact")} | {S("noisePerTurn")} | {S("searchesPerTurn")} | {S("modelCallsPerTurn")} | {S("quiet")}/{S("nothingNeeded")} | {S("breaches")} | {runs} | {S("run")} |");
            }
            md.AppendLine();
        }
        File.WriteAllText(Path.Combine(home, "leaderboard.md"), md.ToString());
    }

    private static (double, double, double) Key(JsonElement r) => (
        -r.GetProperty("servedShare").GetDouble(),
        r.GetProperty("noisePerTurn").GetDouble(),
        r.GetProperty("searchesPerTurn").GetDouble() + 100 * r.GetProperty("modelCallsPerTurn").GetDouble());

    private static string Commit()
    {
        try
        {
            static string Git(string arguments)
            {
                using var git = Process.Start(new ProcessStartInfo("git", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
                var text = git.StandardOutput.ReadToEnd().Trim();
                git.WaitForExit();
                return text;
            }
            return Git("rev-parse HEAD") + (Git("status --porcelain").Length > 0 ? "-dirty" : "");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
