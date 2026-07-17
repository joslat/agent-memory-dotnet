namespace AgentMemory.Nams.Internal;

/// <summary>
/// Validation predicates for <see cref="NamsOptions"/>, called from
/// <see cref="NamsServiceCollectionExtensions.AddNamsAgentMemory"/>'s fluent
/// <c>AddOptions&lt;NamsOptions&gt;().Validate(...)</c> chain -- matching this repository's established
/// convention (every other package's options are validated the same way; see e.g. <c>Neo4jOptions</c>,
/// <c>AgentMemoryMcpOptions</c>) rather than a separate <c>IValidateOptions&lt;NamsOptions&gt;</c>
/// implementation. Extracted to named, individually testable methods (rather than inline lambdas) so each
/// rule has its own regression test without needing to build a full <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// per case.
/// </summary>
internal static class NamsOptionValidator
{
    public static bool HasEndpoint(NamsOptions options) => options.Endpoint is not null;

    /// <summary>
    /// True when <see cref="NamsOptions.Endpoint"/> is missing (deferred to <see cref="HasEndpoint"/>'s own
    /// rule to report), uses HTTPS, or the host has explicitly opted into a non-HTTPS endpoint via
    /// <see cref="NamsOptions.AllowInsecureEndpointForLocalDevelopment"/>.
    /// </summary>
    public static bool HasSecureOrExplicitlyAllowedEndpoint(NamsOptions options) =>
        options.Endpoint is null
        || options.Endpoint.Scheme == Uri.UriSchemeHttps
        || options.AllowInsecureEndpointForLocalDevelopment;

    public static bool HasPositiveRequestTimeout(NamsOptions options) => options.RequestTimeout > TimeSpan.Zero;

    public static bool HasNonNegativeMaxRetryAttempts(NamsOptions options) => options.MaxRetryAttempts >= 0;

    public static bool HasNonNegativeInitialRetryDelay(NamsOptions options) => options.InitialRetryDelay >= TimeSpan.Zero;
}
