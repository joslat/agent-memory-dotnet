namespace AgentMemory.Nams.Domain;

/// <summary>
/// A pure success/failure envelope produced by <see cref="Client.NamsClientExceptionMapper"/>. Keeps response
/// mapping testable independently of the "throw on failure" behavior <c>Neo4jNamsClientAdapter</c> applies on top.
/// </summary>
internal readonly struct NamsOperationResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public NamsFailureKind FailureKind { get; }
    public string? FailureMessage { get; }
    public int? StatusCode { get; }

    private NamsOperationResult(bool isSuccess, T? value, NamsFailureKind failureKind, string? failureMessage, int? statusCode)
    {
        IsSuccess = isSuccess;
        Value = value;
        FailureKind = failureKind;
        FailureMessage = failureMessage;
        StatusCode = statusCode;
    }

    public static NamsOperationResult<T> Success(T value) =>
        new(isSuccess: true, value, failureKind: default, failureMessage: null, statusCode: null);

    public static NamsOperationResult<T> Failure(NamsFailureKind failureKind, string failureMessage, int? statusCode = null) =>
        new(isSuccess: false, value: default, failureKind, failureMessage, statusCode);
}
