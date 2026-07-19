namespace AgentMemory.Nams.Domain;

/// <summary>Coarse classification of a failed NAMS operation, independent of the HTTP status code that produced it.</summary>
internal enum NamsFailureKind
{
    /// <summary>A network-level failure (connection reset, DNS, etc.) with no HTTP response, or a successful
    /// response whose headers arrived but whose body stream failed mid-read (a truncated/reset connection).</summary>
    Network,

    /// <summary>The request timed out before a response was received.</summary>
    Timeout,

    /// <summary>The server responded 429 Too Many Requests.</summary>
    RateLimited,

    /// <summary>The server responded with a 5xx status.</summary>
    ServerError,

    /// <summary>The server responded 401 Unauthorized (missing/expired/invalid credential).</summary>
    Authentication,

    /// <summary>The server responded 403 Forbidden (credential valid, operation not permitted).</summary>
    Authorization,

    /// <summary>The server responded 400/422 or the response body failed to parse into the expected shape.</summary>
    Validation,

    /// <summary>The server responded 404 Not Found.</summary>
    NotFound,

    /// <summary>Any other, unclassified failure.</summary>
    Unknown
}
