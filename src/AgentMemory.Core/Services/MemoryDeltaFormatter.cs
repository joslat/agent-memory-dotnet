using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Security;

namespace AgentMemory.Core.Services;

/// <summary>
/// Renders a <see cref="MemoryDelta"/> as one "what changed" block.
/// </summary>
/// <remarks>
/// <para>
/// Every item goes through the same admission check and the same delimiter as every other recalled
/// category. A delta is recalled memory — it is not a system announcement — and rendering it with any
/// more authority than a recalled fact would grant extraction output a promotion it has not earned.
/// The trust level comes from the item's own metadata, exactly as in
/// <see cref="MemoryContextFormatter"/>: a change is no more trusted for being recent.
/// </para>
/// <para>
/// An empty delta renders <b>nothing</b>, not a heading. "Nothing changed" and "this was consulted and
/// is empty" are different claims, and a bare heading makes the second one.
/// </para>
/// <para>
/// <b>No <c>-&gt;</c> arrows, contrary to the design's sample output.</b> The delimiter escapes every
/// angle bracket in its content — that is how a recalled item is prevented from forging its own closing
/// tag — so the design's <c>was: X -&gt; now: Y</c> would reach the model as <c>was: X -&amp;gt; now:
/// Y</c>. The escaping is not negotiable; the arrow is, so the arrow goes.
/// </para>
/// </remarks>
internal static class MemoryDeltaFormatter
{
    /// <summary>Renders the delta, or an empty string when nothing changed.</summary>
    /// <param name="delta">What changed.</param>
    /// <param name="options">
    /// Admission settings for the built-in check. Ignored when <paramref name="admit"/> is supplied.
    /// </param>
    /// <param name="logger">Receives one warning per excluded item.</param>
    /// <param name="admit">
    /// The admission decision to use, when the caller has one of its own.
    /// </param>
    /// <remarks>
    /// <para>
    /// <paramref name="admit"/> exists because the two adapters do not share an admission mechanism: the
    /// Semantic Kernel path calls <see cref="RecalledMemoryAdmission"/> directly, while the Agent
    /// Framework path routes every item through a host-pluggable <c>IMemoryContextAdmissionPolicy</c>.
    /// Hard-coding the former here would mean a host that installed a custom policy got it applied to
    /// every recalled category <i>except</i> the delta — precisely the silent, category-shaped hole this
    /// codebase has closed twice already.
    /// </para>
    /// </remarks>
    public static string Format(
        MemoryDelta delta,
        MemoryContextFormatterOptions? options = null,
        ILogger? logger = null,
        Func<string, MemoryTrustLevel, bool>? admit = null)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.IsEmpty) return string.Empty;

        var opts = options ?? new MemoryContextFormatterOptions();
        var admitItem = admit ?? ((content, trust) => RecalledMemoryAdmission.ShouldAdmit(
            content, trust, opts.MinimumTrustForAdmissionBypass, opts.Strict));
        var lines = new List<string>();

        void Add(string prefix, string content, MemoryTrustLevel trust)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            // Per item, not per block: one flagged line must not silently drop the unrelated changes
            // rendered alongside it.
            if (!admitItem(content, trust))
            {
                logger?.LogWarning(
                    "Excluded a changed memory item from the delta block: instruction-like content.");
                return;
            }

            lines.Add($"- {prefix}: {content}");
        }

        // A pair is admitted at the LOWER of the two items' trust: the rendered line contains both, so
        // admitting it at the winner's trust would let the loser's text bypass a check it would fail
        // on its own.
        static MemoryTrustLevel Lower(MemoryTrustLevel a, MemoryTrustLevel b) => a <= b ? a : b;

        foreach (var fact in delta.NewFacts)
            Add("New", Describe(fact), Trust(fact));
        foreach (var pair in delta.SupersededPairs)
            Add("Updated", $"was \"{Describe(pair.Old)}\", now \"{Describe(pair.New)}\"",
                Lower(Trust(pair.Old), Trust(pair.New)));
        foreach (var fact in delta.InvalidatedFacts)
            Add("No longer holds", Describe(fact), Trust(fact));
        foreach (var fact in delta.ExpiredValidity)
            Add("Expired", Describe(fact), Trust(fact));
        foreach (var fact in delta.NewlyDueProspective)
            Add("Now due", Describe(fact), Trust(fact));
        foreach (var preference in delta.NewPreferences)
            Add("New preference", $"[{preference.Category}] {preference.PreferenceText}", Trust(preference));
        foreach (var pair in delta.SupersededPreferences)
            Add("Updated preference",
                $"was \"{pair.Old.PreferenceText}\", now \"{pair.New.PreferenceText}\"",
                Lower(Trust(pair.Old), Trust(pair.New)));
        foreach (var entity in delta.NewEntities)
            Add("New entity", $"{entity.Name} ({entity.Type})", Trust(entity));

        if (lines.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.Append("What changed since we last spoke (from ")
            .Append(Stamp(delta.Since))
            .Append(" to ")
            .Append(Stamp(delta.TakenAtUtc))
            .AppendLine("):");
        builder.Append(string.Join("\n", lines));

        // Truncation is stated in the rendered text, not just on the object: the model reading this
        // block is the one that would otherwise conclude it had seen every change.
        if (delta.TruncatedSections.Count > 0)
        {
            builder.Append("\n[truncated: ")
                .Append(string.Join(", ", delta.TruncatedSections))
                .Append(']');
        }

        return RecalledMemoryDelimiter.Wrap("delta", builder.ToString());
    }

    private static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mmZ", CultureInfo.InvariantCulture);

    private static string Describe(Fact fact) => $"{fact.Subject} {fact.Predicate} {fact.Object}";

    private static MemoryTrustLevel Trust(Fact fact) => fact.Metadata.GetTrustLevel();

    private static MemoryTrustLevel Trust(Preference preference) => preference.Metadata.GetTrustLevel();

    private static MemoryTrustLevel Trust(Entity entity) => entity.Metadata.GetTrustLevel();
}
