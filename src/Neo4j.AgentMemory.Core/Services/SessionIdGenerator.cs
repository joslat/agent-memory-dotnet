using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Generates session IDs based on the configured <see cref="SessionStrategy"/>.
/// </summary>
public sealed class SessionIdGenerator : ISessionIdGenerator
{
    private readonly ShortTermMemoryOptions _options;

    public SessionIdGenerator(IOptions<ShortTermMemoryOptions> options)
    {
        _options = options.Value;
    }

    /// <inheritdoc/>
    public string GenerateSessionId(string? userId = null)
    {
        return _options.SessionStrategy switch
        {
            SessionStrategy.PerConversation  => Guid.NewGuid().ToString(),
            SessionStrategy.PerDay          => $"{userId ?? "anonymous"}-{DateTime.UtcNow:yyyy-MM-dd}",
            SessionStrategy.PersistentPerUser => userId
                ?? throw new ArgumentNullException(nameof(userId), "userId is required for PersistentPerUser strategy"),
            _ => throw new ArgumentOutOfRangeException(nameof(_options.SessionStrategy), "Unknown SessionStrategy value")
        };
    }
}
