using System.Diagnostics.CodeAnalysis;

namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Entry point for the fluent error-builder pattern inspired by HotChocolate's ErrorBuilder.
/// </summary>
/// <example>
/// <code>
/// throw MemoryError.Create("Entity not found.")
///     .WithCode(MemoryErrorCodes.EntityNotFound)
///     .WithEntityId(entityId)
///     .WithInner(ex)
///     .Build();
/// </code>
/// </example>
public static class MemoryError
{
    /// <summary>Starts building a <see cref="MemoryException"/> with the given human-readable message.</summary>
    public static MemoryErrorBuilder Create(string message) => new(message);
}

/// <summary>
/// Fluent builder that constructs a richly-annotated <see cref="MemoryException"/>.
/// All <c>With*</c> methods return the same builder instance for chaining.
/// </summary>
public sealed class MemoryErrorBuilder
{
    private readonly string _message;
    private string? _code;
    private Exception? _inner;
    private readonly Dictionary<string, object?> _metadata = new(StringComparer.Ordinal);

    internal MemoryErrorBuilder(string message)
    {
        _message = message;
    }

    /// <summary>Attaches a structured error code (see <see cref="MemoryErrorCodes"/>).</summary>
    public MemoryErrorBuilder WithCode(string code)
    {
        _code = code;
        return this;
    }

    /// <summary>Records the entity ID that caused the error under the <c>entityId</c> metadata key.</summary>
    public MemoryErrorBuilder WithEntityId(string entityId)
        => WithMetadata("entityId", entityId);

    /// <summary>Records a Cypher query that caused the error under the <c>query</c> metadata key.</summary>
    public MemoryErrorBuilder WithQuery(string cypherQuery)
        => WithMetadata("query", cypherQuery);

    /// <summary>Records the session ID under the <c>sessionId</c> metadata key.</summary>
    public MemoryErrorBuilder WithSessionId(string sessionId)
        => WithMetadata("sessionId", sessionId);

    /// <summary>Attaches an inner exception to be set as <see cref="Exception.InnerException"/>.</summary>
    public MemoryErrorBuilder WithInner(Exception inner)
    {
        _inner = inner;
        return this;
    }

    /// <summary>Attaches an arbitrary key/value pair to the exception's metadata.</summary>
    public MemoryErrorBuilder WithMetadata(string key, object? value)
    {
        _metadata[key] = value;
        return this;
    }

    /// <summary>Constructs and returns the configured <see cref="MemoryException"/>. Use with <c>throw</c>.</summary>
    public MemoryException Build()
        => new(_message, _code, _metadata, _inner);

    /// <summary>Constructs the exception and throws it immediately. Equivalent to <c>throw Build()</c>.</summary>
    [DoesNotReturn]
    public void Throw() => throw Build();
}
