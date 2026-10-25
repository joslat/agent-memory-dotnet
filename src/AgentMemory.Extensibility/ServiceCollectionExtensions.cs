using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMemory.Extensibility;

/// <summary>Registers the module SDK.</summary>
[Experimental("AMEXT001")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the context compiler. Contributors are registered separately, by whoever owns them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No catalog, no profile, no resolver.</b> Those belong with hosting, which is a later slice;
    /// a registration call that accepted a composition profile and then ignored it would be worse
    /// than not offering one.
    /// </para>
    /// <para>
    /// <b>The core contributor is not registered here either.</b> On the Agent Framework path core
    /// memory is inherited, not compiled, so registering it would put a second renderer of core
    /// memory into the container for a host that does not want one. A host that genuinely compiles
    /// context — no MAF provider — adds it itself with
    /// <see cref="AddCoreMemoryContributor"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentMemoryExtensibility(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // SCOPED, to match what it consumes. Core registers `IMemoryContextAssembler` scoped, so a
        // singleton compiler or contributor would capture a scoped dependency: with scope validation
        // the graph refuses to build, and without it the captured assembler outlives its request.
        // This repository has paid for a captive dependency once already (the Diffbot typed client).
        services.TryAddScoped<IContextCompiler>(sp => new ContextCompiler(
            sp.GetServices<IContextContributor>(), sp.GetRequiredService<IMemoryIsolationPolicy>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ContextCompiler>>()));
        return services;
    }

    /// <summary>Registers the compiler against an explicitly supplied, successful module resolution.</summary>
    /// <remarks>Replaces the compiler registration; contributors remain separately registered.
    /// The captured metadata supplies frozen telemetry identities, not publisher trust or a runtime lease.
    /// Does not register a catalog, infer an ambient resolution, or install an exporter.</remarks>
    public static IServiceCollection AddAgentMemoryExtensibility(this IServiceCollection services, ModuleResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded) throw new ArgumentException("Successful metadata resolution is required.", nameof(resolution));
        return services.AddResolvedCompiler(_ => resolution);
    }

    /// <summary>
    /// Composes modules statically for the process lifetime (Extensibility 0.12): one call adds each module's
    /// contributors, ingestion providers and options, and the compiler over them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Activation is lazy and fails closed.</b> The first resolve of <see cref="ModuleSet"/> (or of anything built on it:
    /// the compiler, contributors, <see cref="IModuleIngestion"/>) resolves the composition against this host's actual
    /// registrations, binds and validates every module's options, and freezes the set. Any problem throws
    /// <see cref="ModuleActivationException"/> listing all of them; nothing is half-active. Resolve <see cref="ModuleSet"/> at
    /// startup to fail there.
    /// </para>
    /// <para>
    /// <b>Owned identities.</b> A contributor, its sections and an ingestion provider must be in the namespace of the module
    /// that added them; a module cannot speak for another.
    /// </para>
    /// <para>
    /// On the Agent Framework path, add <c>AddAgentMemoryFrameworkExtensibility</c> (and module ingestion or selection) as
    /// before; they use what this registers.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAgentMemoryModules(this IServiceCollection services, Action<ModuleSetBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        if (services.Any(d => d.ServiceType == typeof(ModuleSet)))
            throw new InvalidOperationException("A process composes one module set; AddAgentMemoryModules was already called.");
        var builder = new ModuleSetBuilder();
        configure(builder);

        services.AddSingleton(sp => ModuleSet.Activate(builder, sp));
        services.AddScoped<ModuleSetScope>();
        // One registration per contributor, so hosts and the extensible provider see them as ordinary contributors.
        for (var index = 0; index < ModuleSet.CountContributors(builder); index++)
        {
            var position = index;
            services.AddScoped<IContextContributor>(sp => sp.GetRequiredService<ModuleSetScope>().Contributor(position));
        }
        if (builder.Store is { } store)
            services.Replace(ServiceDescriptor.Scoped<IModuleStore>(sp => store.CreateStore(sp, sp.GetRequiredService<ModuleSet>().Resolution)));
        services.Replace(ServiceDescriptor.Scoped<IModuleIngestion>(sp =>
        {
            var set = sp.GetRequiredService<ModuleSet>();
            var providers = sp.GetRequiredService<ModuleSetScope>().Providers;
            var stores = new Dictionary<string, IModuleStore>(StringComparer.Ordinal);
            if (set.Store is not null)
                foreach (var module in set.ModulesWithProviders) stores[module] = sp.GetRequiredService<IModuleStore>();
            // Store-aware writing (W3): providers see their existing records through their module's views, and the replacements
            // they name are decided by the host's closing approver (Core registers one over its own closing policy).
            var services = new ModuleIngestionServices
            {
                Views = set.Store is null ? null : module => new Reading.ModuleViews(set.Resolution, set.CreateReader(sp, module)),
                Closings = sp.GetService<IMemoryClosingApprover>(),
            };
            return new ModuleIngestion(set.Resolution, providers, stores, sp.GetRequiredService<IMemoryIsolationPolicy>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ModuleIngestion>>(), services);
        }));
        return services.AddResolvedCompiler(sp => sp.GetRequiredService<ModuleSet>().Resolution);
    }

    private static IServiceCollection AddResolvedCompiler(this IServiceCollection services, Func<IServiceProvider, ModuleResolution> resolution)
    {
        services.AddAgentMemoryExtensibility();
        services.Replace(ServiceDescriptor.Scoped<IContextCompiler>(sp => new ContextCompiler(
            resolution(sp), sp.GetServices<IContextContributor>(), sp.GetRequiredService<IMemoryIsolationPolicy>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ContextCompiler>>())));
        return services;
    }

    /// <summary>
    /// Adds a module's context contributor.
    /// </summary>
    /// <remarks>
    /// Contributors are additive, so this appends rather than replacing: two modules both
    /// contributing is the normal case, and a <c>TryAdd</c> here would silently drop the second.
    /// </remarks>
    public static IServiceCollection AddContextContributor<T>(this IServiceCollection services)
        where T : class, IContextContributor
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IContextContributor, T>();
        return services;
    }

    /// <summary>
    /// Adds the core memory contributor, for hosts that COMPILE context rather than inherit it.
    /// </summary>
    /// <remarks>
    /// Deliberately opt-in and deliberately separate from
    /// <see cref="AddAgentMemoryExtensibility(IServiceCollection)"/>. On the Agent Framework path the extensible provider
    /// inherits the shipped one and gets core memory from <c>base</c>; adding this there would render
    /// core twice, once inherited and once compiled, which is the single outcome the opaque core
    /// section exists to prevent.
    /// </remarks>
    public static IServiceCollection AddCoreMemoryContributor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IContextContributor, Contributors.CoreMemoryContextContributor>();
        return services;
    }
}
