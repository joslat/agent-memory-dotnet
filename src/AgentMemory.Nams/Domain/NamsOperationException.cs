namespace AgentMemory.Nams.Domain;

/// <summary>
/// Thrown when a NAMS REST operation fails. Deliberately does not derive from
/// <c>AgentMemory.Abstractions.Exceptions.MemoryException</c> -- <c>AgentMemory.Nams</c> has zero sibling-package
/// references by design (see <c>docs/architecture.md</c> §5, rule B9).
/// </summary>
internal sealed class NamsOperationException : Exception
{
    public NamsFailureKind FailureKind { get; }

    /// <summary>The HTTP status code that produced this failure, if the failure originated from a response
    /// (as opposed to a network-level exception).</summary>
    public int? StatusCode { get; }

    public NamsOperationException(NamsFailureKind failureKind, string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        StatusCode = statusCode;
    }
}
