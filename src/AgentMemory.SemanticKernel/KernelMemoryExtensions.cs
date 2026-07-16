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
        var options = builder.Services.AddOptions<MemoryRecallSecurityOptions>();
        if (configureSecurity is not null)
            options.Configure(configureSecurity);

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
    /// <param name="builder">The kernel builder.</param>
    /// <param name="sessionId">Session to recall within.</param>
    /// <param name="userId">Optional owner/user id (R1). Null ⇒ unscoped recall (all owners).</param>
    /// <param name="securityOptions">
    /// Optional security options (#92 Phase 6) governing <see cref="Neo4jTextSearch.GetTextSearchResultsAsync"/>/
    /// <see cref="Neo4jTextSearch.GetSearchResultsAsync"/>'s per-item admission -- the same concept as
    /// <see cref="AddNeo4jMemoryPlugin(IKernelBuilder, Action{MemoryRecallSecurityOptions}?)"/>'s
    /// <c>configureSecurity</c>, but taken directly here rather than via a configure delegate/DI options,
    /// since <see cref="Neo4jTextSearch"/> itself is constructed per-call with an explicit
    /// <paramref name="sessionId"/>/<paramref name="userId"/> rather than resolved as a shared singleton.
    /// Defaults to <see cref="MemoryContextSecurityMode.Permissive"/> when omitted.
    /// </param>
    public static IKernelBuilder AddNeo4jTextSearch(
        this IKernelBuilder builder, string sessionId, string? userId = null,
        MemoryRecallSecurityOptions? securityOptions = null)
    {
        builder.Services.AddTransient<Neo4jTextSearch>(sp =>
            new Neo4jTextSearch(sp.GetRequiredService<IMemoryService>(), sessionId, userId, securityOptions));
        return builder;
    }
}
