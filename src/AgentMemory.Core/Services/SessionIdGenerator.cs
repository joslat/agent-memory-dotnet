using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Generates session IDs based on the configured <see cref="SessionStrategy"/>.
/// </summary>
public sealed class SessionIdGenerator : ISessionIdGenerator
{
    private readonly ShortTermMemoryOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionIdGenerator"/> class.
    /// </summary>
    /// <param name="options">The short-term memory options used to determine the session strategy.</param>
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
