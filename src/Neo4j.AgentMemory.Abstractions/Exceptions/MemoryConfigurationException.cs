namespace Neo4j.AgentMemory.Abstractions.Exceptions;

/// <summary>
/// Thrown when configuration validation fails (invalid options).
/// </summary>
public class MemoryConfigurationException : MemoryException
{
    /// <summary>The name of the option that failed validation, if applicable.</summary>
    public string? OptionName { get; }

    /// <summary>Initializes a new instance with the specified message.</summary>
    /// <param name="message">The error message.</param>
    public MemoryConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with the specified message and option name.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="optionName">The option name that failed validation.</param>
    public MemoryConfigurationException(string message, string optionName)
        : base(message)
    {
        OptionName = optionName;
    }

    /// <summary>Initializes a new instance with the specified message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public MemoryConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
