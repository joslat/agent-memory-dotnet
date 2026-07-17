using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMemory.AgentFramework.Nams;

/// <summary>
/// Dependency injection extensions for the NAMS-backed MAF adapter.
/// </summary>
public static class NamsAgentFrameworkServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NamsMemoryContextProvider"/>. Requires <c>AddNamsAgentMemory(...)</c> (for the
    /// NAMS client/resolver/recall/persistence services) and <c>AddAgentMemoryFramework(...)</c> (for the
    /// shared, backend-neutral <c>ContextFormatOptions</c>/<c>IAutomaticRecallPolicy</c>/
    /// <c>IMemoryContextAdmissionPolicy</c> registrations) to have been called first -- matching the
    /// engineering plan's own DI registration example. If either is missing, resolving
    /// <see cref="NamsMemoryContextProvider"/> fails with a standard, clear "service not registered" DI
    /// error rather than a custom validation message; this is the same failure mode every other missing-
    /// prerequisite-registration case in this repo produces, not a special case invented for this package.
    /// </summary>
    public static IServiceCollection AddNamsAgentMemoryFramework(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<NamsMemoryContextProvider>();

        return services;
    }
}
