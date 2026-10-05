using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
        services.AddOptions<MemoryGateOptions>()
            .Configure(configure)
            // A configuration binds any integer to an enum; an undefined mode would silently be none of the three.
            .Validate(o => Enum.IsDefined(o.Mode), "MemoryGateOptions.Mode must be Floor, Judge or Everything.")
            .Validate(o => o.Threshold is >= 0 and <= 1, "MemoryGateOptions.Threshold must be between 0 and 1.")
            .Validate(o => o.Timeout > TimeSpan.Zero, "MemoryGateOptions.Timeout must be positive.")
            .Validate(o => o.WideLimit > 0, "MemoryGateOptions.WideLimit must be positive.");
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

    /// <summary>The configuration section <see cref="AddAgentMemoryGate(IServiceCollection, IConfiguration, Action{MemoryGateOptions}?)"/> reads by convention.</summary>
    public const string SectionName = "AgentMemory:RetrievalRouter";

    /// <summary>
    /// The same, with the settings from a configuration section (by convention <see cref="SectionName"/>), then
    /// <paramref name="configure"/>. The section names the properties of <see cref="MemoryGateOptions"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// // "AgentMemory": { "RetrievalRouter": { "Mode": "Judge", "Threshold": 0.23, "Timeout": "00:00:03",
    /// //   "Judges": [ { "Name": "jev", "Endpoint": "https://api.typesafe.ai/v1/systemone", "KeyVariable": "TYPESAFE_API_KEY", "Weight": 0.8 } ] } }
    /// services.AddAgentMemoryGate(configuration.GetSection(GateServiceCollectionExtensions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddAgentMemoryGate(
        this IServiceCollection services, IConfiguration section, Action<MemoryGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddAgentMemoryGate(o =>
        {
            section.Bind(o);
            configure?.Invoke(o);
        });
    }

    private static IMemoryContextAssembler Inner(IServiceProvider sp, ServiceDescriptor original) =>
        original.ImplementationFactory is { } factory ? (IMemoryContextAssembler)factory(sp)
        : original.ImplementationInstance is IMemoryContextAssembler instance ? instance
        : (IMemoryContextAssembler)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
}
