using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
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
        services.TryAddSingleton<IContextCompiler, ContextCompiler>();
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

        services.AddSingleton<IContextContributor, T>();
        return services;
    }

    /// <summary>
    /// Adds the core memory contributor, for hosts that COMPILE context rather than inherit it.
    /// </summary>
    /// <remarks>
    /// Deliberately opt-in and deliberately separate from
    /// <see cref="AddAgentMemoryExtensibility"/>. On the Agent Framework path the extensible provider
    /// inherits the shipped one and gets core memory from <c>base</c>; adding this there would render
    /// core twice, once inherited and once compiled, which is the single outcome the opaque core
    /// section exists to prevent.
    /// </remarks>
    public static IServiceCollection AddCoreMemoryContributor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IContextContributor, Contributors.CoreMemoryContextContributor>();
        return services;
    }
}
