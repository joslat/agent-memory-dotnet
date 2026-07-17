namespace AgentMemory.Nams.Client;

/// <summary>Configures the <see cref="HttpClient"/> used by <see cref="Neo4jNamsClientAdapter"/> from
/// <see cref="NamsOptions"/>.</summary>
internal static class NamsClientFactory
{
    public static void ConfigureHttpClient(HttpClient httpClient, NamsOptions options)
    {
        httpClient.BaseAddress = NormalizeBaseAddress(options.Endpoint);
        httpClient.Timeout = options.RequestTimeout;
    }

    /// <summary>
    /// Ensures the base address ends with a trailing <c>/</c> so relative request paths combine correctly.
    /// <see cref="Uri"/> combination drops the last path segment of a base URI that doesn't end in <c>/</c> --
    /// e.g. base <c>https://host/v1</c> + relative <c>conversations</c> resolves to <c>https://host/conversations</c>,
    /// silently dropping <c>/v1</c>. <c>NamsOptionValidator.HasAbsoluteEndpoint</c> guarantees
    /// <see cref="NamsOptions.Endpoint"/> is absolute before this runs.
    /// </summary>
    internal static Uri NormalizeBaseAddress(Uri endpoint) =>
        endpoint.OriginalString.EndsWith('/') ? endpoint : new Uri(endpoint.OriginalString + "/");
}
