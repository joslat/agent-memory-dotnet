using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 40.66: the two clocks a TypedMemEval question asks on, so a run can see what the library does with both.
/// </summary>
/// <remarks>
/// <para>
/// Without this, recall ran at the question's own date on the valid clock and at the machine's now on the transaction
/// clock: right for a corpus ingested moments ago, and blind to bitemporal behaviour. "Which city does the record show
/// for Alice in February?" was answered as of the question's date, and "As of 19 January, which city did the record
/// show?" could not be asked of the store at all.
/// </para>
/// <para>
/// <b>Valid time</b> is the time the question names, read by the library's own parser (what a deployment with
/// <c>ResolveTemporalQueries</c> does), after removing a leading <c>"As of &lt;date&gt;,"</c>, which names belief, not
/// truth. <b>Transaction time</b> is the corpus's own <c>asof_instant</c> when the question asks about belief then, else
/// the question's date. Both need the store's transaction times to be the sessions' dates: <see cref="ReplayClock"/>.
/// AgentEval's entry model drops the <c>typedmemeval</c> block, so it is read here from the corpus as shipped.
/// </para>
/// </remarks>
internal static class TypedMemEvalClocks
{
    /// <summary>What the corpus says a question asks on: the clock it tests and, for belief, the instant.</summary>
    internal sealed record Asked(string Question, string? Clock, DateTimeOffset? AsOfInstant);

    private static readonly Regex BeliefPrefix = new(
        @"^\s*as\s+of\s+[^,?]+,\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Each question's clocks, by question id, from the embedded corpus.</summary>
    internal static IReadOnlyDictionary<string, Asked> Read(TypedMemEvalVertical vertical)
    {
        using var document = JsonDocument.Parse(TypedMemEvalCorpus.ReadJson(vertical));
        var asked = new Dictionary<string, Asked>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = entry.GetProperty("question_id").GetString()!;
            string? clock = null;
            DateTimeOffset? instant = null;
            if (entry.TryGetProperty("typedmemeval", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("clock", out var c) && c.ValueKind == JsonValueKind.String) clock = c.GetString();
                if (meta.TryGetProperty("asof_instant", out var a) && a.ValueKind == JsonValueKind.String) instant = ParseDate(a.GetString()!);
            }
            asked[id] = new Asked(entry.GetProperty("question").GetString() ?? string.Empty, clock, instant);
        }
        return asked;
    }

    /// <summary>The valid-time and transaction-time instants to recall at.</summary>
    internal static (DateTimeOffset ValidAsOf, DateTimeOffset SystemAsOf) Resolve(
        string question, DateTimeOffset questionDate, Asked? asked)
    {
        var text = BeliefPrefix.Replace(asked?.Question ?? question, string.Empty, 1);
        var valid = TemporalQueryParser.Resolve(text, questionDate) ?? questionDate;
        var system = asked?.AsOfInstant ?? questionDate;
        return (valid, system);
    }

    /// <summary>The corpus's date style: <c>2026/01/19 (Mon) 18:15</c>, UTC.</summary>
    internal static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.ParseExact(value, "yyyy/MM/dd (ddd) HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}

/// <summary>
/// 40.66: a clock the harness moves, so the store stamps each replayed session at its own date (G1 makes every stamp
/// read it) and recall runs at the question's date.
/// </summary>
internal sealed class ReplayClock : IClock
{
    /// <summary>The instant the clock reads; set before each session and before each question.</summary>
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => Now;
}
