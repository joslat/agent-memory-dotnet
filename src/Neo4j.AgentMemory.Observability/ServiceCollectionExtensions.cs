using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Observability;

/// <summary>
/// Extension methods for adding Agent Memory observability to the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OpenTelemetry-based observability decorators for Agent Memory services.
    /// Wraps registered <see cref="IMemoryService"/> and <see cref="IGraphRagContextSource"/>
    /// with tracing spans and metrics collection.
    /// </summary>
    /// <remarks>
    /// Uses manual decoration (no Scrutor dependency) to keep the package lightweight.
    /// Call this after registering the core memory services.
    /// </remarks>
    public static IServiceCollection AddAgentMemoryObservability(this IServiceCollection services)
    {
        services.TryAddSingleton<MemoryMetrics>();

        DecorateMemoryService(services);
        DecorateGraphRagContextSource(services);
        DecorateEntityExtractor(services);
        DecorateFactExtractor(services);
        DecoratePreferenceExtractor(services);
        DecorateRelationshipExtractor(services);
        DecorateEnrichmentService(services);

        return services;
    }

    private static void DecorateMemoryService(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IMemoryService>(services);
        if (descriptor is null)
        {
            return;
        }

        services.Remove(descriptor);

        services.Add(new ServiceDescriptor(
            typeof(IMemoryService),
            provider =>
            {
                var inner = CreateInstance<IMemoryService>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedMemoryService(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static void DecorateGraphRagContextSource(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IGraphRagContextSource>(services);
        if (descriptor is null)
        {
            return;
        }

        services.Remove(descriptor);

        services.Add(new ServiceDescriptor(
            typeof(IGraphRagContextSource),
            provider =>
            {
                var inner = CreateInstance<IGraphRagContextSource>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedGraphRagContextSource(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static ServiceDescriptor? FindDescriptor<T>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(T))
            {
                return services[i];
            }
        }

        return null;
    }

    private static T CreateInstance<T>(IServiceProvider provider, ServiceDescriptor descriptor)
        where T : notnull
    {
        if (descriptor.ImplementationInstance is T instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (T)descriptor.ImplementationFactory(provider);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (T)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException(
            $"Cannot resolve inner service for {typeof(T).Name}. " +
            "The service descriptor has no implementation instance, factory, or type.");
    }

    private static void DecorateEntityExtractor(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IEntityExtractor>(services);
        if (descriptor is null) return;
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IEntityExtractor),
            provider =>
            {
                var inner = CreateInstance<IEntityExtractor>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedEntityExtractor(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static void DecorateFactExtractor(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IFactExtractor>(services);
        if (descriptor is null) return;
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IFactExtractor),
            provider =>
            {
                var inner = CreateInstance<IFactExtractor>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedFactExtractor(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static void DecoratePreferenceExtractor(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IPreferenceExtractor>(services);
        if (descriptor is null) return;
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IPreferenceExtractor),
            provider =>
            {
                var inner = CreateInstance<IPreferenceExtractor>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedPreferenceExtractor(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static void DecorateRelationshipExtractor(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IRelationshipExtractor>(services);
        if (descriptor is null) return;
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IRelationshipExtractor),
            provider =>
            {
                var inner = CreateInstance<IRelationshipExtractor>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedRelationshipExtractor(inner, metrics);
            },
            descriptor.Lifetime));
    }

    private static void DecorateEnrichmentService(IServiceCollection services)
    {
        var descriptor = FindDescriptor<IEnrichmentService>(services);
        if (descriptor is null) return;
        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(
            typeof(IEnrichmentService),
            provider =>
            {
                var inner = CreateInstance<IEnrichmentService>(provider, descriptor);
                var metrics = provider.GetRequiredService<MemoryMetrics>();
                return new InstrumentedEnrichmentService(inner, metrics);
            },
            descriptor.Lifetime));
    }
}
