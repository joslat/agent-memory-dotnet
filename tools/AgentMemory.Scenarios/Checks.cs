using System.Globalization;

namespace AgentMemory.Scenarios;

/// <summary>The scenario's deterministic checks: what reached the prompt, and what the store holds. No model grades.</summary>
internal static class Checks
{
    /// <summary>
    /// A rough token count: four characters per token. Good enough to compare memories with each other, which is all
    /// it is used for.
    /// </summary>
    public static int Tokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>True when <paramref name="text"/> contains <paramref name="phrase"/>, ignoring case and accents.</summary>
    public static bool Mentions(string text, string phrase) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            text, phrase, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>
    /// The prompt's items: its lines, and each line's entries where a memory joined several with "; " (AgentMemory's
    /// "Known facts: a; b; c"). One item is one statement or one fact.
    /// </summary>
    public static IReadOnlyList<string> Items(string prompt) =>
        prompt.Split('\n')
            .SelectMany(line => line.Split("; "))
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();

    /// <summary>The first item that has every group of <paramref name="mentions"/>, or <see langword="null"/>.</summary>
    public static string? ItemWith(IReadOnlyList<string> items, IReadOnlyList<IReadOnlyList<string>> mentions) =>
        items.FirstOrDefault(item => mentions.All(spellings => spellings.Any(spelling => Mentions(item, spelling))));

    /// <summary>Each need, with the prompt item that holds it (null when no single item does).</summary>
    public static IReadOnlyList<NeedResult> Needs(Question question, string prompt)
    {
        var items = Items(prompt);
        return question.Needs.Select(need => new NeedResult(need.Fact, ItemWith(items, need.Mentions))).ToList();
    }

    /// <summary>Each store check, with the live facts that satisfy it (empty when none does).</summary>
    public static IReadOnlyList<StoreResult> Store(IEnumerable<StoreCheck> checks, IReadOnlyList<StoredFact> facts) =>
        checks.Select(check =>
        {
            var byText = facts.Where(fact => check.Mentions.All(spellings => spellings.Any(s => Mentions(fact.Text, s)))).ToList();
            var dated = byText.Where(fact =>
                    (check.Date is not { } on || fact.Starts == on) &&
                    (check.From is not { } from || fact.ValidFrom == from) &&
                    (check.Until is not { } until || fact.ValidUntil == until))
                .ToList();
            return new StoreResult(check.Fact, dated.Select(f => f.Describe()).ToList(), byText.Select(f => f.Describe()).ToList());
        }).ToList();
}

internal sealed record NeedResult(string Fact, string? FoundIn)
{
    public bool Found => FoundIn is not null;
}

/// <summary>
/// One store check: <see cref="Matching"/> are the live facts with the right words and the right dates;
/// <see cref="SameWords"/> are those with the right words whatever their dates, so a wrong date shows as a wrong date
/// rather than a missing fact.
/// </summary>
internal sealed record StoreResult(string Fact, IReadOnlyList<string> Matching, IReadOnlyList<string> SameWords)
{
    public bool Held => Matching.Count > 0;
}

/// <summary>A live fact as the store holds it, read straight from Neo4j.</summary>
internal sealed record StoredFact(
    string Subject, string Predicate, string Object, DateOnly? OccurredOn, DateOnly? ValidFrom, DateOnly? ValidUntil, string? Category)
{
    public string Text => $"{Subject} {Predicate} {Object}";

    /// <summary>The day it happens or begins.</summary>
    public DateOnly? Starts => OccurredOn ?? ValidFrom;

    public string Describe()
    {
        var when = OccurredOn is { } on ? $" [on {on:yyyy-MM-dd}]"
            : ValidFrom is { } from ? $" [{from:yyyy-MM-dd} → {(ValidUntil is { } until ? until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "…")}]"
            : ValidUntil is { } end ? $" [until {end:yyyy-MM-dd}]" : "";
        return $"{Subject} · {Predicate} · {Object}{when}";
    }
}
