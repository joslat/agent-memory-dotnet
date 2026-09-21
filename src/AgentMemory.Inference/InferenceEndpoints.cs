namespace AgentMemory.Inference;

/// <summary>
/// Endpoint rules: what may be printed, and what may carry a key.
/// </summary>
/// <remarks>
/// Both halves exist because an endpoint is operator-supplied and an API key travels to it. One
/// decides what a banner or a diagnostic is allowed to echo; the other decides whether the key is
/// allowed to leave the machine in cleartext.
/// </remarks>
public static class InferenceEndpoints
{
    /// <summary>
    /// Reduces an endpoint to scheme, host and port — dropping user-info, path, query and fragment.
    /// </summary>
    /// <remarks>
    /// <b>All four of those parts are places a credential turns up in the wild</b>, and a gateway
    /// URL carrying a token in its path is the common one: <c>https://gw.example/v1/sk-live-…</c>
    /// reads as an ordinary endpoint and is a secret. Anything printed — banner, diagnostic, log,
    /// exception — goes through here first. An endpoint that cannot be parsed returns a placeholder
    /// rather than the original string, because "unparseable" is not the same as "safe to echo".
    /// </remarks>
    public static string Sanitize(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "(unset)";

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return "(invalid endpoint)";

        // IsDefaultPort keeps the common case readable: https://api.example.com, not :443.
        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// Whether an endpoint may carry an API key: https anywhere, or http to loopback only.
    /// </summary>
    /// <remarks>
    /// <b>The loopback exception is what makes a local Ollama or LM Studio usable</b> — they serve
    /// plain http on 127.0.0.1 and there is no wire to sniff. Anything else on http would put the
    /// key on the network in cleartext, so it is refused rather than warned about: a warning on a
    /// machine nobody is watching is the same as no warning.
    /// </remarks>
    /// <param name="endpoint">The operator-supplied endpoint.</param>
    /// <param name="variableName">The variable it came from, named in the error.</param>
    /// <param name="error">Why it was refused. Never contains the endpoint's path, query or user-info.</param>
    public static bool TryValidate(string? endpoint, string variableName, out string? error)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            error = $"{variableName} is not set.";
            return false;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            error = $"{variableName} is not an absolute URI.";
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.IsLoopback)
            {
                error = null;
                return true;
            }

            error =
                $"{variableName} uses http to a non-loopback host ({Sanitize(endpoint)}), which would "
                + "send the API key in cleartext. Use https, or point at localhost for a local server.";
            return false;
        }

        error = $"{variableName} must use https (or http to localhost); got scheme '{uri.Scheme}'.";
        return false;
    }
}
