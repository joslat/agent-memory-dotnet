using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentMemory.Nams.Internal;

namespace AgentMemory.Nams;

/// <summary>
/// DI registration for the additive NAMS backend skeleton. Registers only <see cref="NamsOptions"/>
/// (configured, validated, <c>ValidateOnStart</c>) and a <see cref="NamsBackendDescriptor"/> singleton --
/// nothing else exists yet in this phase (no client, no recall/persistence). Calling this method is the
/// ONLY way anything NAMS-related gets registered; no other package in this repository references
/// <c>AgentMemory.Nams</c>, so an application that never calls this method is completely unaffected.
/// </summary>
public static class NamsServiceCollectionExtensions
{
    /// <summary>Registers the NAMS backend's configuration surface.</summary>
    public static IServiceCollection AddNamsAgentMemory(
        this IServiceCollection services, Action<NamsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NamsOptions>()
            .Configure(configure)
            .Validate(NamsOptionValidator.HasEndpoint, "NamsOptions.Endpoint must be provided.")
            .Validate(
                NamsOptionValidator.HasSecureOrExplicitlyAllowedEndpoint,
                "NamsOptions.Endpoint must use HTTPS unless AllowInsecureEndpointForLocalDevelopment is set to true.")
            .Validate(NamsOptionValidator.HasPositiveRequestTimeout, "NamsOptions.RequestTimeout must be positive.")
            .Validate(NamsOptionValidator.HasNonNegativeMaxRetryAttempts, "NamsOptions.MaxRetryAttempts must be non-negative.")
            .Validate(NamsOptionValidator.HasNonNegativeInitialRetryDelay, "NamsOptions.InitialRetryDelay must be non-negative.")
            .ValidateOnStart();

        // Idempotent by construction: TryAddSingleton with a fixed instance means a second
        // AddNamsAgentMemory call is a harmless no-op for this registration (the options registration
        // above is likewise safe to call more than once -- each call just adds another equivalent
        // configure/validate step).
        services.TryAddSingleton(NamsBackendDescriptor.Instance);

        return services;
    }
}
