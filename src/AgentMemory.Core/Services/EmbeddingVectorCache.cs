using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.Options;

namespace AgentMemory.Core.Services;

/// <summary>
/// A bounded, thread-safe, least-recently-used map from embedding input text to its vector, shared by
/// every orchestrator in a container. Sized by <see cref="MemoryOptions.EmbeddingCacheCapacity"/>;
/// capacity 0 (the default) makes every call a miss and stores nothing.
/// </summary>
internal sealed class EmbeddingVectorCache
{
    /// <summary>A cache that never remembers anything.</summary>
    public static readonly EmbeddingVectorCache Disabled = new(0);

    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Text, float[] Vector)>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Text, float[] Vector)> _recency = new();
    private long _hits;
    private long _misses;

    public EmbeddingVectorCache(IOptions<MemoryOptions> options)
        : this(options.Value.EmbeddingCacheCapacity)
    {
    }

    internal EmbeddingVectorCache(int capacity) => _capacity = Math.Max(0, capacity);

    public int Capacity => _capacity;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);

    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    public bool TryGet(string text, out float[] vector)
    {
        if (_capacity == 0)
        {
            vector = [];
            return false;
        }

        lock (_gate)
        {
            if (_index.TryGetValue(text, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                vector = node.Value.Vector;
                _hits++;
                return true;
            }
            _misses++;
        }
        vector = [];
        return false;
    }

    /// <summary>Remembers a vector. Empty vectors (failed generations) are never remembered.</summary>
    public void Set(string text, float[] vector)
    {
        if (_capacity == 0 || vector is not { Length: > 0 })
            return;

        lock (_gate)
        {
            if (_index.TryGetValue(text, out var existing))
            {
                _recency.Remove(existing);
                _index.Remove(text);
            }
            _index[text] = _recency.AddFirst((text, vector));
            while (_index.Count > _capacity && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _index.Remove(oldest.Value.Text);
            }
        }
    }
}
