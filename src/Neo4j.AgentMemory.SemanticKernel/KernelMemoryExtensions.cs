using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Neo4j.AgentMemory.Abstractions.Services;

#pragma warning disable SKEXP0001

namespace Neo4j.AgentMemory.SemanticKernel;

/// <summary>Extension methods for registering the Neo4j Agent Memory plugin with Semantic Kernel.</summary>
public static class KernelMemoryExtensions
{
    /// <summary>
    /// Registers <see cref="Neo4jMemoryPlugin"/> with the kernel builder's DI container
    /// and adds it as a named plugin called <c>Neo4jMemory</c>.
    /// <see cref="IMemoryService"/> must already be registered in the service collection.
    /// </summary>
    public static IKernelBuilder AddNeo4jMemoryPlugin(this IKernelBuilder builder)
    {
        builder.Services.AddTransient<Neo4jMemoryPlugin>();
        builder.Plugins.AddFromType<Neo4jMemoryPlugin>("Neo4jMemory");
        return builder;
    }

    /// <summary>
    /// Adds a <see cref="Neo4jMemoryPlugin"/> to an already-built <see cref="Kernel"/>.
    /// Useful when constructing the kernel outside of a DI-driven pipeline.
    /// </summary>
    public static Kernel AddNeo4jMemoryPlugin(this Kernel kernel, IMemoryService memoryService)
    {
        var plugin = new Neo4jMemoryPlugin(memoryService);
        kernel.Plugins.AddFromObject(plugin, "Neo4jMemory");
        return kernel;
    }

    /// <summary>
    /// Registers a <see cref="Neo4jTextSearch"/> instance for the given session in the
    /// kernel builder's DI container.
    /// </summary>
    public static IKernelBuilder AddNeo4jTextSearch(this IKernelBuilder builder, string sessionId)
    {
        builder.Services.AddTransient<Neo4jTextSearch>(sp =>
            new Neo4jTextSearch(sp.GetRequiredService<IMemoryService>(), sessionId));
        return builder;
    }
}
