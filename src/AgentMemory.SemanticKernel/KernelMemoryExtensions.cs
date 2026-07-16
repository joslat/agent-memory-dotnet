using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using AgentMemory.Abstractions.Services;

#pragma warning disable SKEXP0001

namespace AgentMemory.SemanticKernel;

/// <summary>Extension methods for registering the Neo4j Agent Memory plugin with Semantic Kernel.</summary>
public static class KernelMemoryExtensions
{
    /// <summary>
    /// Registers <see cref="Neo4jMemoryPlugin"/> with the kernel builder's DI container
    /// and adds it as a named plugin called <c>Neo4jMemory</c>.
    /// <see cref="IMemoryService"/> must already be registered in the service collection.
    /// </summary>
    /// <param name="builder">The kernel builder.</param>
    /// <param name="configureSecurity">
    /// Optional configuration for <see cref="MemoryRecallSecurityOptions"/> (#92 Phase 6) -- the
    /// instruction-like-content admission mode and trust-bypass threshold applied to the <c>recall</c>
    /// function's output. Omit to use the defaults (<see cref="MemoryContextSecurityMode.Permissive"/>).
    /// </param>
    public static IKernelBuilder AddNeo4jMemoryPlugin(
        this IKernelBuilder builder, Action<MemoryRecallSecurityOptions>? configureSecurity = null)
    {
        if (configureSecurity is not null)
            builder.Services.AddOptions<MemoryRecallSecurityOptions>().Configure(configureSecurity);
        else
            builder.Services.AddOptions<MemoryRecallSecurityOptions>();

        builder.Services.AddTransient<Neo4jMemoryPlugin>();
        builder.Plugins.AddFromType<Neo4jMemoryPlugin>("Neo4jMemory");
        return builder;
    }

    /// <summary>
    /// Adds a <see cref="Neo4jMemoryPlugin"/> to an already-built <see cref="Kernel"/>.
    /// Useful when constructing the kernel outside of a DI-driven pipeline.
    /// </summary>
    public static Kernel AddNeo4jMemoryPlugin(
        this Kernel kernel, IMemoryService memoryService, MemoryRecallSecurityOptions? securityOptions = null)
    {
        var plugin = new Neo4jMemoryPlugin(memoryService, securityOptions: Options.Create(securityOptions ?? new MemoryRecallSecurityOptions()));
        kernel.Plugins.AddFromObject(plugin, "Neo4jMemory");
        return kernel;
    }

    /// <summary>
    /// Registers a <see cref="Neo4jTextSearch"/> instance for the given session (and optional owner) in
    /// the kernel builder's DI container. Pass <paramref name="userId"/> in multi-tenant hosts so the SK
    /// text-search tool recalls only that owner's plus shared memory (R1); null ⇒ unscoped (all owners).
    /// </summary>
    public static IKernelBuilder AddNeo4jTextSearch(this IKernelBuilder builder, string sessionId, string? userId = null)
    {
        builder.Services.AddTransient<Neo4jTextSearch>(sp =>
            new Neo4jTextSearch(sp.GetRequiredService<IMemoryService>(), sessionId, userId));
        return builder;
    }
}
