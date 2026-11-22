using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Services;

internal sealed partial class MemoryContextAssembler
{
    /// <summary>Connections followed per question, at most.</summary>
    internal const int MaxConnectionsFollowed = 3;

    /// <summary>Words of a predicate that name no relation ("is", "of", "the"), so they never match a question.</summary>
    private static readonly HashSet<string> ConnectionSmallWords = new(StringComparer.Ordinal)
    {
        "is", "are", "was", "were", "be", "been", "has", "have", "had", "of", "the", "a", "an", "to", "in", "at", "on",
        "for", "with", "by", "and", "or", "my", "your", "our", "her", "his", "their", "its", "user", "does", "did", "do",
        "who", "what", "where", "when", "how", "which", "why",
    };

    /// <summary>
    /// SPEECH-RELATION-RECALL (<see cref="RecallOptions.MaxConnectionFacts"/>): the live facts of whoever a recalled fact
    /// connects to the owner by a relation the question names, one hop, bounded. Off at 0, and then nothing is read.
    /// </summary>
    private async Task<IReadOnlyList<Fact>> ExpandConnectionsAsync(
        IReadOnlyList<Fact> facts, LookAheadResult lookAhead, ProspectiveDueResult due, string question, RecallOptions options,
        MemoryScope scope, CancellationToken cancellationToken, TimeSpan? remaining)
    {
        if (options.MaxConnectionFacts == 0 || options.MaxFacts <= 0 || string.IsNullOrWhiteSpace(question) || facts.Count == 0)
            return facts;
        if (options.MaxConnectionFacts is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(options), "Connection fact cap must be between 0 and 32.");
        if (remaining is { } left && left <= TimeSpan.Zero) return facts;

        var asked = ConnectionWords(question);
        var others = Connections(facts, asked).Take(MaxConnectionsFollowed).ToList();
        if (others.Count == 0) return facts;

        var seen = facts.Concat(lookAhead.Window).Concat(lookAhead.Upcoming).Concat(lookAhead.Overlapping)
            .Concat(due.Due).Concat(due.Expiring).Select(fact => fact.FactId).ToHashSet(StringComparer.Ordinal);
        using var expiry = remaining is not null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (expiry is not null) expiry.CancelAfter(remaining!.Value);
        var token = expiry?.Token ?? cancellationToken;
        var now = _clock.GetUtcNow();
        var widened = new List<Fact>(facts);
        var added = 0;
        try
        {
            foreach (var other in others)
            {
                var theirs = await TimedAsync("memory.recall.connection-facts", () => _longTerm.GetFactsBySubjectAsync(
                    other, scope, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // Their live facts only, newest first: closed ones and those whose time has ended are not "now".
                foreach (var fact in theirs.Where(f => f.InvalidatedAtUtc is null && (f.ValidUntil is null || f.ValidUntil > now))
                             .OrderByDescending(f => f.CreatedAtUtc).ThenBy(f => f.FactId, StringComparer.Ordinal))
                {
                    if (added >= options.MaxConnectionFacts) break;
                    if (!seen.Add(fact.FactId)) continue;
                    widened.Add(fact);
                    added++;
                }
                if (added >= options.MaxConnectionFacts) break;
            }
        }
        catch (OperationCanceledException) when (expiry is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
        {
            // Out of time: what was found so far stands, nothing is waited for.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return added == 0 ? facts : widened;
    }

    /// <summary>
    /// The other side of each recalled fact that connects the owner by a relation the question names, in recall order,
    /// each once: "Anna | sister of | user" gives "Anna" for "where does my sister live?"; "user | works at | Acme" gives
    /// "Acme" for "what does my work do?". The owner is "user" on either side, or a first-person word as the subject.
    /// </summary>
    internal static IEnumerable<string> Connections(IEnumerable<Fact> facts, IReadOnlySet<string> asked)
    {
        var given = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var ownerSubject = MemoryUserNames.IsSelf(fact.Subject);
            var ownerObject = MemoryTripleCanonicalizer.CanonicalValue(fact.Object) is "user" or "the user";
            var other = ownerSubject && !ownerObject ? fact.Object : ownerObject && !ownerSubject ? fact.Subject : null;
            if (string.IsNullOrWhiteSpace(other) || other.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) continue;
            if (!ConnectionWords(fact.Predicate).Any(word => asked.Any(a => SameWord(a, word)))) continue;
            var key = MemoryTripleCanonicalizer.CanonicalValue(other);
            if (key.Length > 0 && given.Add(key)) yield return other.Trim();
        }
    }

    private static HashSet<string> ConnectionWords(string? text) =>
        WordSplit().Split((text ?? "").ToLowerInvariant())
            .Select(word => word.EndsWith("'s", StringComparison.Ordinal) ? word[..^2] : word.Trim('\''))
            .Where(word => word.Length >= 3 && !ConnectionSmallWords.Contains(word))
            .ToHashSet(StringComparer.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\p{L}\p{Nd}']+")]
    private static partial System.Text.RegularExpressions.Regex WordSplit();

    /// <summary>One word, or one the start of the other with at least four letters in common ("work", "works").</summary>
    private static bool SameWord(string a, string b) =>
        a == b || Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));
}
