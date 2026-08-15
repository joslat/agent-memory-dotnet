using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Attaches the sentence a fact came from, restoring what the triple dropped.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured loss.</b> A triple keeps subject, predicate and object and throws away tense,
/// participants and ordinals — three separately named failing questions. The hybrid arm fixes all
/// three by brute force, carrying whole transcripts at roughly six times the tokens (2,505 against
/// 403 per question). The source sentence is already reachable: <c>SourceMessageIds</c> is on every
/// fact, entity and preference, and the benchmark harness already dereferences it for dates. The prize
/// is structured accuracy at around 500 tokens rather than hybrid's 2,505, which is why every cap here
/// is deliberate rather than defensive.
/// </para>
/// <para>
/// <b>Shortest containing sentence, not the whole message.</b> A message can be a paragraph; the
/// clause that earns its place is the one that mentions the object. Shortest-containing is a cheap
/// proxy for "most specific" and bounds the token cost by construction.
/// </para>
/// <para>
/// The repository is optional for the DI reason documented on <see cref="SupersessionProjectionFeature"/>.
/// </para>
/// </remarks>
internal sealed class SourceQuoteProjectionFeature(IMessageRepository? messages) : IProjectionFeature
{
    private static readonly char[] SentenceTerminators = ['.', '!', '?', '\n'];

    public bool IsEnabled(MemoryProjectionOptions options) => options.AttachSourceQuotes && messages is not null;

    public async Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (messages is null) return;

        var sources = await state.GetSourceMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        if (sources.Count == 0) return;

        var options = state.Options;
        var attached = 0;

        foreach (var fact in state.Facts)
        {
            if (attached >= options.MaxQuotesPerRecall) break;

            var quote = SelectQuote(fact, sources, options.MaxQuoteLength);
            if (quote is null) continue;

            state.Annotate(fact.FactId, annotation => annotation with { SourceQuote = quote });
            attached++;
        }
    }

    /// <summary>The shortest source sentence containing this fact's object, or null.</summary>
    internal static string? SelectQuote(
        Fact fact, IReadOnlyDictionary<string, Message> sources, int maxLength)
    {
        string? best = null;

        foreach (var id in fact.SourceMessageIds)
        {
            if (!sources.TryGetValue(id, out var message)) continue;
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            foreach (var raw in message.Content.Split(SentenceTerminators, StringSplitOptions.RemoveEmptyEntries))
            {
                var sentence = raw.Trim();
                if (sentence.Length == 0) continue;
                if (sentence.IndexOf(fact.Object, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best is null || sentence.Length < best.Length) best = sentence;
            }
        }

        if (best is null) return null;

        // Skip when the item's own rendered text already contains the sentence: repeating it spends
        // tokens to say the same thing twice, which is precisely the cost this feature is priced
        // against.
        var triple = $"{fact.Subject} {fact.Predicate} {fact.Object}";
        if (triple.Contains(best, StringComparison.OrdinalIgnoreCase)) return null;

        return best.Length <= maxLength ? best : best[..maxLength].TrimEnd() + "…";
    }
}
