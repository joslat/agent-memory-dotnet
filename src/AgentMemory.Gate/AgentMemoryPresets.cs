using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Gate;

/// <summary>
/// Chooses a host's preset from configuration: <c>AgentMemory:Preset</c> (Recommended, Conversational or LowLevel;
/// Recommended when absent), checked against the retrieval judges configured under
/// <see cref="GateServiceCollectionExtensions.SectionName"/>. Recommended with no judge runs as Conversational.
/// </summary>
/// <example>
/// <code>
/// var preset = AgentMemoryPresets.Choose(builder.Configuration);
/// builder.Services.AddNeo4jAgentMemory(preset, neo4j => { ... });
/// builder.Services.AddAgentMemoryGate(preset, builder.Configuration);
/// builder.Services.AddAgentMemoryFramework(o => o.ApplyPreset(preset.Effective));
/// </code>
/// </example>
public static class AgentMemoryPresets
{
    /// <summary>The configuration key that names the preset.</summary>
    public const string PresetKey = "AgentMemory:Preset";

    /// <summary>The preset <paramref name="configuration"/> asks for, decided against the judges it configures.</summary>
    /// <exception cref="InvalidOperationException">The configured preset is not one this library knows.</exception>
    public static MemoryPresetChoice Choose(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var named = configuration[PresetKey];
        MemoryPreset requested;
        if (string.IsNullOrWhiteSpace(named))
            requested = MemoryPreset.Recommended;
        else if (!Enum.TryParse(named.Trim(), ignoreCase: true, out requested) || !Enum.IsDefined(requested))
            throw new InvalidOperationException(
                $"{PresetKey} is '{named}', which is not a preset: use Recommended, Conversational or LowLevel.");

        // The judges the gate would actually use: bound as the gate binds them, counted by its own rule (an endpoint and
        // a weight above zero). A judge the gate would skip is not a judge here either, or Recommended would be chosen
        // and then recall on the similarity floor.
        var gate = new MemoryGateOptions();
        configuration.GetSection(GateServiceCollectionExtensions.SectionName).Bind(gate);
        return MemoryPresetChoice.Decide(requested, SystemOneMemoryGate.UsableJudges(gate).Count());
    }

    /// <summary>
    /// Registers the retrieval gate when <paramref name="preset"/> runs a judge, bound from
    /// <see cref="GateServiceCollectionExtensions.SectionName"/> after the Recommended gate half; registers nothing
    /// otherwise, so Conversational and the low-level defaults recall exactly as without the gate.
    /// </summary>
    public static IServiceCollection AddAgentMemoryGate(
        this IServiceCollection services, MemoryPresetChoice preset, IConfiguration configuration,
        Action<MemoryGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!preset.UsesJudge) return services;
        // The Recommended half first, then the section, so what the host configured (a judge's weight, Floor mode for an
        // A/B) wins over the preset; then the code's own overrides.
        var section = configuration.GetSection(GateServiceCollectionExtensions.SectionName);
        return services.AddAgentMemoryGate(gate =>
        {
            gate.ApplyRecommended();
            section.Bind(gate);
            configure?.Invoke(gate);
        });
    }
}
