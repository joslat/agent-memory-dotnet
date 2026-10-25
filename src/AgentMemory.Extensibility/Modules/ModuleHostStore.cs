using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Trusted host binding to the service scope used by one profile store.</summary>
/// <remarks>The caller owns the scope lifetime. Keyed bindings never fall back to unkeyed services.
/// These bindings are host configuration, not module-supplied declarations.</remarks>
[Experimental("AMEXT001")]
public sealed record ModuleHostStore
{
    /// <summary>Registered provider identity, matched ordinally against the profile.</summary>
    public required string Provider { get; init; }
    /// <summary>Effective service provider for this store, including its bound options.</summary>
    public required IServiceProvider Services { get; init; }
    /// <summary>Exact service key, or null for the unkeyed store.</summary>
    public string? ServiceKey { get; init; }
    /// <summary>Trusted storage adapter probe; absence cannot grant ModuleSchema.</summary>
    public IModuleSchemaCapabilityProbe? SchemaProbe { get; init; }
    /// <summary>The chat model the host grants to modules that declare <see cref="ModuleHostFeatures.Inference"/>; null grants none.</summary>
    public Func<IServiceProvider, Microsoft.Extensions.AI.IChatClient>? ChatClient { get; init; }
}

/// <summary>Storage adapter inspection used before module construction or migration.</summary>
/// <remarks>Inspect registrations only: no module factories, connections, DDL or model calls.
/// A successful probe is a capability check, not database readiness.</remarks>
[Experimental("AMEXT001")]
public interface IModuleSchemaCapabilityProbe
{
    /// <summary>Provider identity whose requirements this adapter understands.</summary>
    string Provider { get; }
    /// <summary>Reports missing storage registrations at the exact service key.</summary>
    IReadOnlyList<string> GetProblems(IServiceProvider services, string? serviceKey = null);
}
