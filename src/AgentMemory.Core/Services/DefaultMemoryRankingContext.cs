using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default <see cref="IWritableMemoryRankingContext"/>. <see cref="Current"/> is backed by an
/// <see cref="AsyncLocal{T}"/> so this can be a process-wide singleton yet stay safe under concurrency:
/// each recall flow sees its own per-request ranking override (set by the assembler from the recall's
/// <c>Intent</c>), and concurrent recalls cannot bleed into one another (mirrors the owner/store contexts).
/// </summary>
internal sealed class DefaultMemoryRankingContext : IWritableMemoryRankingContext
{
    private readonly AsyncLocal<MemoryRankingOptions?> _current = new();

    /// <inheritdoc />
    public MemoryRankingOptions? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
