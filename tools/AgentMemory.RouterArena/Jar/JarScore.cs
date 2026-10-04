using System.Numerics;
using System.Text.Json.Serialization;

namespace AgentMemory.RouterArena.Jar;

/// <summary>
/// A form's score on a set of turns, at the prompt: <see cref="Served"/> = turns whose every needed memory was let in, of
/// the <see cref="Servable"/> ones; clutter = memories let in that the turn neither needs nor accepts, per turn and as a
/// share of the unneeded memories found; the median time a turn (the wide search plus the judge's recorded time).
/// </summary>
public sealed record JarResult(
    [property: JsonPropertyName("served")] int Served,
    [property: JsonPropertyName("servable")] int Servable,
    [property: JsonPropertyName("servedPct")] double ServedPct,
    [property: JsonPropertyName("clutterPerTurn")] double ClutterPerTurn,
    [property: JsonPropertyName("clutterPct")] double ClutterPct,
    [property: JsonPropertyName("msP50")] double? MsP50 = null);

/// <summary>A cross-validated estimate and the form each fold chose.</summary>
public sealed record JarEstimate(JarResult Result, IReadOnlyList<JarForm> Chosen);

/// <summary>
/// The jar's rules (root PLAN 40.86, 40.89): every setting chosen at a clutter budget by the most served, ties to less
/// clutter; estimated by five-fold cross-validation on the training turns (folds by the turn id's sha256); frozen on all
/// of them; read once on a test set. Each candidate form is scored once per turn; a fold only sums.
/// </summary>
public static class JarScore
{
    /// <summary>The share of the unneeded memories found that a form may let in.</summary>
    public static readonly IReadOnlyDictionary<string, double> Budgets = new Dictionary<string, double>
    {
        ["lean"] = 4.0, ["balanced"] = 6.0, ["generous"] = 15.0,
    };

    /// <summary>The wide search, measured warm, and the small local gate on this GPU: the recorded times' floor.</summary>
    public const double SearchMs = 39.0, LocalMs = 21.0;

    /// <summary>Thresholds, low ones too: a judge whose probabilities sit near zero needs thresholds below 0.02.</summary>
    public static readonly IReadOnlyList<double> Grid =
        [0.001, 0.002, 0.003, 0.005, 0.007, 0.01, 0.015, .. Enumerable.Range(2, 94).Select(x => x / 100.0)];

    public static JarResult Score(JarForm form, IReadOnlyList<JarTurn> turns)
    {
        int served = 0, servable = 0, clutter = 0, clutterFound = 0;
        var times = new List<double>();
        foreach (var turn in turns)
        {
            var (admitted, seconds) = form.Admit(turn);
            servable += turn.Servable ? 1 : 0;
            served += turn.Servable && turn.ServedBy(admitted) ? 1 : 0;
            clutter += admitted.Count(m => !turn.Useful.Contains(m));
            clutterFound += turn.ClutterFound;
            times.Add(form.Family == "today" ? 0 : SearchMs + 1000 * seconds + (form.IsLocal && seconds == 0 ? LocalMs : 0));
        }
        times.Sort();
        return new JarResult(served, servable, Math.Round(100.0 * served / Math.Max(1, servable), 1),
            Math.Round((double)clutter / turns.Count, 2), Math.Round(100.0 * clutter / Math.Max(1, clutterFound), 1),
            times.Count > 0 ? Math.Round(times[times.Count / 2]) : null);
    }

    /// <summary>Every setting a family is chosen from, in the order ties are kept (the first best wins).</summary>
    public static IEnumerable<JarForm> Candidates(string family, string? judge = null, string? local = null, string? online = null)
    {
        switch (family)
        {
            case "today":
                yield return JarForm.Today;
                break;
            case "gate" or "module":
                foreach (var t in Grid)
                    yield return new JarForm { Family = family, Judge = judge, T = t };
                break;
            case "blend":
                foreach (var w in new[] { 0.5, 0.6, 0.7, 0.8, 0.9 })
                    foreach (var t in Grid)
                        yield return new JarForm { Family = family, Local = local, Online = online, W = w, T = t };
                break;
            case "hybrid":
                foreach (var low in new[] { 0.005, 0.01, 0.02, 0.05, 0.1, 0.15, 0.2, 0.25 })
                    foreach (var high in new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9 })
                        if (high > low)
                            foreach (var t in new[] { 0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5 })
                                yield return new JarForm { Family = family, Local = local, Online = online, Low = low, High = high, T = t };
                break;
            case "cospool":
                for (var k = 1; k <= 60; k++)
                    yield return new JarForm { Family = family, K = k };
                break;
            case "cosfloor":
                for (var f = 30; f <= 95; f++)
                    yield return new JarForm { Family = family, F = f / 100.0 };
                break;
            case "cosdoor":
                for (var k = 1; k <= 15; k++)
                    yield return new JarForm { Family = family, K = k };
                break;
            default:
                throw new ArgumentException($"unknown family '{family}'", nameof(family));
        }
    }

    /// <summary>One row per form, one cell per turn: served (0/1), servable (0/1), clutter let in, clutter found.</summary>
    public static (int Served, int Servable, int Clutter, int Found)[][] Table(IReadOnlyList<JarForm> forms, IReadOnlyList<JarTurn> turns) =>
        [.. forms.Select(form => turns.Select(turn =>
        {
            var (admitted, _) = form.Admit(turn);
            return (turn.Servable && turn.ServedBy(admitted) ? 1 : 0, turn.Servable ? 1 : 0, admitted.Count(m => !turn.Useful.Contains(m)), turn.ClutterFound);
        }).ToArray())];

    /// <summary>The form with the most served on the turns at <paramref name="index"/> within the budget; ties to less clutter.</summary>
    public static int? Pick(IReadOnlyList<(int Served, int Servable, int Clutter, int Found)[]> rows, IReadOnlyList<int> index, double budget)
    {
        int? best = null;
        (int Served, double Pct) bestKey = default;
        for (var f = 0; f < rows.Count; f++)
        {
            int served = 0, clutter = 0, found = 0;
            foreach (var i in index)
            {
                served += rows[f][i].Served;
                clutter += rows[f][i].Clutter;
                found += rows[f][i].Found;
            }
            var pct = 100.0 * clutter / Math.Max(1, found);
            if (pct > budget)
                continue;
            if (best is null || served > bestKey.Served || (served == bestKey.Served && pct < bestKey.Pct))
            {
                best = f;
                bestKey = (served, pct);
            }
        }
        return best;
    }

    /// <summary>Choose on four folds, score on the fifth, five times; the pooled held-fold result. Null when no setting fits.</summary>
    public static JarEstimate? CrossValidate(IReadOnlyList<JarForm> forms, IReadOnlyList<JarTurn> turns, double budget)
    {
        var rows = Table(forms, turns);
        int served = 0, servable = 0, clutter = 0, found = 0;
        var chosen = new List<JarForm>();
        for (var k = 0; k < 5; k++)
        {
            var train = Enumerable.Range(0, turns.Count).Where(i => turns[i].Fold != k).ToList();
            var held = Enumerable.Range(0, turns.Count).Where(i => turns[i].Fold == k).ToList();
            if (Pick(rows, train, budget) is not { } f)
                return null;
            chosen.Add(forms[f]);
            foreach (var i in held)
            {
                served += rows[f][i].Served;
                servable += rows[f][i].Servable;
                clutter += rows[f][i].Clutter;
                found += rows[f][i].Found;
            }
        }
        return new JarEstimate(new JarResult(served, servable, Math.Round(100.0 * served / servable, 1),
            Math.Round((double)clutter / turns.Count, 2), Math.Round(100.0 * clutter / found, 1)), chosen);
    }

    /// <summary>The setting chosen on every training turn: what is frozen.</summary>
    public static JarForm? Choose(IReadOnlyList<JarForm> forms, IReadOnlyList<JarTurn> turns, double budget) =>
        Pick(Table(forms, turns), [.. Enumerable.Range(0, turns.Count)], budget) is { } f ? forms[f] : null;

    /// <summary>Turn by turn against today: served by the form and not by today, and the reverse.</summary>
    public static (int Wins, int Losses) AgainstToday(JarForm form, IReadOnlyList<JarTurn> turns)
    {
        int wins = 0, losses = 0;
        foreach (var turn in turns.Where(t => t.Servable))
        {
            var mine = turn.ServedBy(form.Admit(turn).Admitted);
            var today = turn.ServedBy(turn.Today);
            wins += mine && !today ? 1 : 0;
            losses += today && !mine ? 1 : 0;
        }
        return (wins, losses);
    }

    /// <summary>The two-sided exact sign test.</summary>
    public static double SignTest(int wins, int losses)
    {
        var n = wins + losses;
        if (n == 0)
            return 1.0;
        var tail = BigInteger.Zero;
        var choose = BigInteger.One;
        for (var i = 0; i <= Math.Min(wins, losses); i++)
        {
            tail += choose;
            choose = choose * (n - i) / (i + 1);
        }
        return Math.Min(1.0, Math.Exp(BigInteger.Log(2 * tail) - n * Math.Log(2)));
    }
}
