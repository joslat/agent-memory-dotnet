using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Fact-grained evidence coverage: of the distinctive VALUES the gold-bearing turns carry, how many
/// reached the recalled facts?
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this fills.</b> Every diagnostic we and AgentEval hold is session-grained —
/// <c>RequiredEvidenceSessionCount</c>, <c>…InAnswerContext</c>, <c>ComponentCoverage</c> (which is
/// session-INDEXED and reproduces the session metric exactly). Row 56 registered a session-grained
/// check as the confirmation test for a fact-grained mechanism and it nearly missed a real effect:
/// six of eleven gained questions had coverage 4/4 → 4/4, and one LOST question's coverage IMPROVED.
/// Row 58 then produced two losses that volume cannot explain at all — <c>tme-prc-045</c> lost a
/// required session while gaining ONE fact.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> A permissive "distinctive token" rule would match prose and report a
/// high number everywhere, which is how an instrument stops being able to fail. Only two token
/// classes count, both unambiguous and both central to the shapes that collapsed: <b>amounts</b>
/// (currency and bare decimals) and <b>integer quantities</b>. Names are excluded — the corpora use
/// invented multi-word names whose casing rules are the formatter's, not the fact's, and a matcher
/// tuned on them would be tuned on padding.
/// </para>
/// <para>
/// <b>This measures RETRIEVAL, not answering.</b> A value present in the recalled facts and still
/// absent from the answer is a model failure; a value absent from the facts could never have been
/// answered. Keeping those apart is the whole reason for the metric.
/// </para>
/// </remarks>
internal static class LongMemEvalGoldValueCoverage
{
    /// <summary>
    /// One pass over numbers: optional <c>$</c>, optional thousands separators, optional decimal.
    /// </summary>
    /// <remarks>
    /// Deliberately ONE pattern rather than an amount rule plus an integer rule. Two rules produced
    /// overlapping matches — the integer rule matched the digit groups INSIDE a decimal, so
    /// "371.41 and 354.06 and 388.24" reported nine values instead of three, and a two-rule amount
    /// pattern anchored on <c>\d{1,3}</c> could not match a bare "1113.71" at all: it matched the
    /// tail "3.71" and reported a miss for a value that was present. Both were caught by the tests
    /// below before this instrument was pointed at any data.
    /// <para>
    /// The lookarounds keep a match from starting or stopping inside a longer token, which is what
    /// made the two-rule version overlap in the first place.
    /// </para>
    /// </remarks>
    private static readonly Regex Number = new(
        @"(?<![\w.])\$?\d[\d,]*(?:\.\d+)?(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The values a text carries, normalised so <c>$1,113.71</c> and <c>1113.71</c> are one value.
    /// </summary>
    /// <remarks>
    /// Normalisation is what makes the comparison meaningful: the gold answer writes an amount with
    /// a currency symbol and separators, and an extracted fact very often does not. Comparing the
    /// raw strings would report a miss for a value that is present, which biases the metric toward
    /// "retrieval failed" — the conclusion it exists to test.
    /// </remarks>
    internal static IReadOnlySet<string> Extract(string? text)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return values;

        foreach (Match m in Number.Matches(text))
        {
            // A lone one-digit integer matches ordinary prose ("step 3", "2 of them") and would
            // inflate every question. An amount or a multi-digit quantity does not.
            var isAmount = m.Value.Contains('.', StringComparison.Ordinal)
                           || m.Value.Contains('$', StringComparison.Ordinal);
            var digits = m.Value.Count(char.IsDigit);
            if (!isAmount && digits < 2) continue;
            if (Normalise(m.Value) is { } n) values.Add(n);
        }

        return values;
    }

    private static string? Normalise(string raw)
    {
        var cleaned = raw.Replace("$", string.Empty, StringComparison.Ordinal)
                         .Replace(",", string.Empty, StringComparison.Ordinal)
                         .Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d.ToString("0.####", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Coverage of the gold values by the recalled facts. Null when the gold text carries no value
    /// this rule recognises.
    /// </summary>
    /// <remarks>
    /// <b>Null is not zero.</b> A question whose gold is "the Ennisk pass runs first" has no amount
    /// and no quantity, so its coverage is UNMEASURABLE by this instrument rather than 0.0. Folding
    /// the two together would report every ordering question as a total retrieval failure and drag
    /// the mean toward a conclusion the data never supported — the constant-column failure this
    /// project has hit three times.
    /// </remarks>
    internal static GoldValueCoverage? Measure(string? goldText, IEnumerable<string?> recalledFactTexts)
    {
        ArgumentNullException.ThrowIfNull(recalledFactTexts);

        var required = Extract(goldText);
        if (required.Count == 0) return null;

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in recalledFactTexts)
            available.UnionWith(Extract(fact));

        var present = required.Count(available.Contains);
        return new GoldValueCoverage(required.Count, present);
    }
}

/// <summary>How many of a question's gold values the recalled facts actually carried.</summary>
/// <param name="RequiredValues">Distinct values the gold text carries.</param>
/// <param name="PresentValues">How many of them appear in the recalled facts.</param>
internal readonly record struct GoldValueCoverage(int RequiredValues, int PresentValues)
{
    /// <summary>Fraction present. <c>RequiredValues</c> is never zero — <c>Measure</c> returns null then.</summary>
    public double Fraction => (double)PresentValues / RequiredValues;

    /// <summary>True when every gold value was retrievable, so a wrong answer is not a retrieval miss.</summary>
    public bool IsComplete => PresentValues == RequiredValues;
}

/// <summary>
/// Collects per-question gold-value coverage across a run, so a vertical can be read as
/// "retrieval never had it" versus "retrieval had it and the answer still missed".
/// </summary>
/// <remarks>
/// <para>
/// <b>Required values come from the gold-bearing TURNS, not the gold answer.</b> That distinction is
/// the whole correction. The gold answer for an aggregation question states the RESULT ("$1,113.71
/// in total"), and the result is never stored -- the components are. Measured against the answer
/// string, this reported a miss on questions the engine got RIGHT.
/// <c>LongMemEvalMessageOrigin.HasAnswer</c> marks the turns carrying the components, and those are
/// the values retrieval actually had to deliver.
/// </para>
/// <para>
/// <b>Record-only.</b> Nothing here influences retrieval, the prompt, or the answer; it reads what
/// recall already returned. A measurement that changed the thing it measures would void every
/// pairing in this wave.
/// </para>
/// </remarks>
public sealed class LongMemEvalGoldValueCoverageProbe
{
    private readonly List<GoldValueSample> _samples = [];
    private readonly object _gate = new();

    /// <summary>Records one question. Gold carrying no measurable value is skipped, never scored 0.</summary>
    public void Record(string questionId, IEnumerable<string?> goldTurnTexts, IEnumerable<string?> recalledFactTexts)
    {
        ArgumentNullException.ThrowIfNull(goldTurnTexts);
        ArgumentNullException.ThrowIfNull(recalledFactTexts);

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var turn in goldTurnTexts)
            required.UnionWith(LongMemEvalGoldValueCoverage.Extract(turn));
        if (required.Count == 0) return;

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in recalledFactTexts)
            available.UnionWith(LongMemEvalGoldValueCoverage.Extract(fact));

        var sample = new GoldValueSample(questionId, required.Count, required.Count(available.Contains));
        lock (_gate) { _samples.Add(sample); }
    }

    /// <summary>A snapshot, so a caller cannot observe the list mutating mid-read.</summary>
    public IReadOnlyList<GoldValueSample> Samples
    {
        get { lock (_gate) { return _samples.ToArray(); } }
    }
}

/// <param name="QuestionId">The question these values belong to.</param>
/// <param name="RequiredValues">Distinct values the gold-bearing turns carry.</param>
/// <param name="PresentValues">How many of them the recalled facts carried.</param>
public readonly record struct GoldValueSample(string QuestionId, int RequiredValues, int PresentValues)
{
    /// <summary>True when retrieval delivered every value, so a wrong answer is not a retrieval miss.</summary>
    public bool IsComplete => PresentValues == RequiredValues;
}

