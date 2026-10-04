using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Gate;

/// <summary>Adds the gate to an AgentMemory registration.</summary>
[Experimental("AMGATE001")]
public static class GateServiceCollectionExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IMemoryContextAssembler"/> with the gate and registers the judges. Call after the
    /// AgentMemory registration (it must already have registered the assembler).
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddAgentMemoryGate(gate =>
    /// {
    ///     gate.Judges.Add(new SystemOneEndpoint { Name = "jev", Endpoint = new("https://api.typesafe.ai/v1/systemone"), KeyVariable = "TYPESAFE_API_KEY", Weight = 0.8 });
    ///     gate.Judges.Add(new SystemOneEndpoint { Name = "laya", Endpoint = new("http://127.0.0.1:8765/v1/systemone"), Weight = 0.2 });
    ///     gate.UpdateJudge = true;
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddAgentMemoryGate(this IServiceCollection services, Action<MemoryGateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var original = services.LastOrDefault(d => d.ServiceType == typeof(IMemoryContextAssembler))
            ?? throw new InvalidOperationException(
                "AddAgentMemoryGate wraps the memory context assembler: register AgentMemory first, then add the gate.");
        services.Configure(configure);
        services.TryAddSingleton(_ => new SystemOneClient(new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = TimeSpan.FromSeconds(30) }));
        services.TryAddSingleton<IMemoryGate>(sp => new SystemOneMemoryGate(
            sp.GetRequiredService<SystemOneClient>(), sp.GetRequiredService<IOptions<MemoryGateOptions>>(),
            sp.GetService<ILogger<SystemOneMemoryGate>>() ?? NullLogger<SystemOneMemoryGate>.Instance,
            sp.GetService<IEmbeddingGenerator<string, Embedding<float>>>()));
        services.TryAddSingleton<IMemoryUpdateJudge>(sp => new SystemOneUpdateJudge(
            sp.GetRequiredService<SystemOneClient>(), sp.GetRequiredService<IOptions<MemoryGateOptions>>()));

        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(typeof(IMemoryContextAssembler), sp => new GatedMemoryContextAssembler(
            Inner(sp, original), sp.GetRequiredService<IMemoryGate>(), sp.GetRequiredService<IOptions<MemoryGateOptions>>(),
            sp.GetService<ILogger<GatedMemoryContextAssembler>>() ?? NullLogger<GatedMemoryContextAssembler>.Instance,
            sp.GetService<IOptions<AgentMemory.Abstractions.Options.MemoryOptions>>(), sp.GetService<IClock>()), original.Lifetime));
        return services;
    }

    private static IMemoryContextAssembler Inner(IServiceProvider sp, ServiceDescriptor original) =>
        original.ImplementationFactory is { } factory ? (IMemoryContextAssembler)factory(sp)
        : original.ImplementationInstance is IMemoryContextAssembler instance ? instance
        : (IMemoryContextAssembler)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
}
