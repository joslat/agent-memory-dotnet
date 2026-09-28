using AgentMemory.Abstractions.Domain;

namespace AgentMemory.AgentFramework;

/// <summary>
/// 36.5. The turns held back from extraction because the user only asked (<see cref="AgentFrameworkOptions.DeferQuestionTurns"/>),
/// per owner, until a turn that tells something is extracted with them as its own.
/// </summary>
/// <remarks>
/// Deferred, never dropped: a question can carry information ("what should I get my sister Ana, who loves pottery?"),
/// and it is extracted with the next turn that is, or when <see cref="AgentFrameworkOptions.MaxDeferredTurns"/> are
/// waiting. Held in memory for the host's lifetime: what waits when the process stops is not extracted.
/// </remarks>
internal sealed class DeferredTurns
{
    private readonly Dictionary<string, List<Message>> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _turns = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Holds <paramref name="turn"/> unless <paramref name="max"/> turns already wait for <paramref name="key"/>.</summary>
    internal bool TryHold(string key, IReadOnlyList<Message> turn, int max)
    {
        lock (_gate)
        {
            var waiting = _turns.GetValueOrDefault(key);
            if (waiting >= max) return false;
            if (!_held.TryGetValue(key, out var messages)) _held[key] = messages = [];
            messages.AddRange(turn);
            _turns[key] = waiting + 1;
            return true;
        }
    }

    /// <summary>What waits for <paramref name="key"/>, in the order it was said, and no longer held.</summary>
    internal IReadOnlyList<Message> Release(string key)
    {
        lock (_gate)
        {
            _turns.Remove(key);
            return _held.Remove(key, out var messages) ? messages : [];
        }
    }
}
