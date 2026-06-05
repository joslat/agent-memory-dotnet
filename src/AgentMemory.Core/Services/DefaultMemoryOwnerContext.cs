using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Default <see cref="IWritableMemoryOwnerContext"/>. <see cref="UserId"/> is backed by an
/// <see cref="AsyncLocal{T}"/> so this can be registered as a process-wide singleton yet remain safe
/// under concurrency: each request/agent-run flow sees its own value (set e.g. by the MAF provider for
/// that turn), and concurrent flows cannot bleed into one another (IC8, mirrors the store context).
/// </summary>
public sealed class DefaultMemoryOwnerContext : IWritableMemoryOwnerContext
{
    private readonly AsyncLocal<string?> _userId = new();

    /// <inheritdoc />
    public string? UserId
    {
        get => _userId.Value;
        set => _userId.Value = value;
    }
}
