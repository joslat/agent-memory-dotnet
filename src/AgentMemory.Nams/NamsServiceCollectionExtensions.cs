using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Authentication;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Internal;

namespace AgentMemory.Nams;

/// <summary>
/// DI registration for the NAMS backend. Registers <see cref="NamsOptions"/> (configured, validated,
/// <c>ValidateOnStart</c>), a <see cref="NamsBackendDescriptor"/> singleton, and (as of Phase 2) the
/// low-level REST client (<see cref="INamsClient"/>) plus its credential provider and authentication
/// handler -- no recall/persistence integration into the memory pipeline yet (that's Phases 3+). Calling
/// this method is the ONLY way anything NAMS-related gets registered; no other package in this repository
/// references <c>AgentMemory.Nams</c>, so an application that never calls this method is completely
/// unaffected.
/// </summary>
public static class NamsServiceCollectionExtensions
{
    /// <summary>Registers the NAMS backend's configuration surface and low-level REST client.</summary>
    public static IServiceCollection AddNamsAgentMemory(
        this IServiceCollection services, Action<NamsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NamsOptions>()
            .Configure(configure)
            .Validate(NamsOptionValidator.HasEndpoint, "NamsOptions.Endpoint must be provided.")
            .Validate(NamsOptionValidator.HasAbsoluteEndpoint, "NamsOptions.Endpoint must be an absolute URI.")
            .Validate(
                NamsOptionValidator.HasSecureOrExplicitlyAllowedEndpoint,
                "NamsOptions.Endpoint must use HTTPS unless AllowInsecureEndpointForLocalDevelopment is set to true.")
            .Validate(NamsOptionValidator.HasPositiveRequestTimeout, "NamsOptions.RequestTimeout must be positive.")
            .Validate(NamsOptionValidator.HasNonNegativeMaxRetryAttempts, "NamsOptions.MaxRetryAttempts must be non-negative.")
            .Validate(NamsOptionValidator.HasNonNegativeInitialRetryDelay, "NamsOptions.InitialRetryDelay must be non-negative.")
            .Validate(NamsOptionValidator.HasApiKey, "NamsOptions.ApiKey must be provided (JWT/Auth0 authentication is not yet supported).")
            .ValidateOnStart();

        // Idempotent by construction: TryAddSingleton means a second AddNamsAgentMemory call is a
        // harmless no-op for this registration (the options registration above is likewise safe to call
        // more than once -- each call just adds another equivalent configure/validate step).
        services.TryAddSingleton<NamsBackendDescriptor>();

        services.TryAddSingleton<INamsAccessTokenProvider, StaticApiKeyNamsAccessTokenProvider>();
        services.TryAddTransient<NamsAuthenticationHandler>();
        services.AddHttpClient<INamsClient, Neo4jNamsClientAdapter>((serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<NamsOptions>>().Value;
                NamsClientFactory.ConfigureHttpClient(httpClient, options);
            })
            .AddHttpMessageHandler<NamsAuthenticationHandler>();

        return services;
    }
}
