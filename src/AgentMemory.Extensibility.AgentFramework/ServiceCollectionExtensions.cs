using System.Diagnostics.CodeAnalysis;
using AgentMemory.AgentFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMemory.Extensibility.AgentFramework;

/// <summary>Registers the extensible Agent Framework provider.</summary>
[Experimental("AMEXT001")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ExtensibleMemoryContextProvider"/> and makes it the provider a host
    /// resolving <see cref="Neo4jMemoryContextProvider"/> gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The substitution is the point, and without it this package does nothing.</b> A host that
    /// registered the core memory services, added the SDK and added its module contributors would
    /// resolve a compiler, resolve its contributors, and still hand the agent the shipped provider —
    /// so module sections would never reach a prompt, silently, while every registration call
    /// appeared to succeed. That is the reachable-but-never-fed defect in registration form.
    /// </para>
    /// <para>
    /// <b>Replace, not add.</b> Two providers registered against the same service type would both
    /// run when a host resolved <c>IEnumerable</c>, and core memory would be rendered twice. The
    /// descriptor is replaced so there is exactly one provider, and because the extensible one
    /// INHERITS the shipped one, a host resolving the base type still gets a working provider — with
    /// the base's behaviour unchanged when no modules are registered.
    /// </para>
    /// <para>
    /// Scoped, matching the lifetimes the core registration uses for what this depends on.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentMemoryFrameworkExtensibility(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAgentMemoryExtensibility();

        services.TryAddScoped<ExtensibleMemoryContextProvider>();

        // Anything asking for the shipped provider gets the extended one. It IS one, by inheritance,
        // so this cannot narrow what a caller receives.
        services.Replace(ServiceDescriptor.Scoped<Neo4jMemoryContextProvider>(
            sp => sp.GetRequiredService<ExtensibleMemoryContextProvider>()));

        return services;
    }
    /// <summary>Explicitly enables automatic module ingestion after successful message persistence.</summary>
    /// <remarks>
    /// Registering IModuleIngestion alone does not enable this path. Without this call there is no
    /// module callback, source projection, pipeline resolution or write. The factory must bind an
    /// authorized, fixed module composition and resolve scoped services from its supplied provider.
    /// Background work uses a fresh scope and the captured owner/application. This static binding
    /// does not implement runtime module leases or hot replacement.
    /// </remarks>
    public static IServiceCollection AddAgentMemoryFrameworkModuleIngestion(
        this IServiceCollection services,
        Func<IServiceProvider, Ingestion.IModuleIngestion> createIngestion)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createIngestion);
        services.AddAgentMemoryFrameworkExtensibility();
        services.Replace(ServiceDescriptor.Singleton(new ModuleTurnIngestionBinding(createIngestion)));
        services.TryAddScoped<ModuleTurnIngestion>(sp => new ModuleTurnIngestion(
            sp, sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ModuleTurnIngestionBinding>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentFrameworkOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleTurnIngestion>>(),
            sp.GetService<IBackgroundExtraction>()));
        return services;
    }

}
