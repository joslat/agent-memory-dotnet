namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Generates a session ID based on the configured <see cref="Options.SessionStrategy"/>.
/// </summary>
public interface ISessionIdGenerator
{
    /// <summary>
    /// Generates a session ID, optionally scoped to a user.
    /// </summary>
    string GenerateSessionId(string? userId = null);
}
