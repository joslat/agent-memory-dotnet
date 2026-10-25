using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Ingestion;
using Microsoft.Extensions.Options;

namespace AgentMemory.Extensibility.Modules;

/// <summary>A module's entry point: what it contributes, declared once when the host composes it (spec §4.1).</summary>
/// <remarks>
/// A module assembly carries its manifest and one public, parameterless implementation, added with
/// <see cref="ModuleSetBuilder.Add{TModule}"/>. <see cref="Configure"/> runs at registration, before any service exists,
/// so it only declares: options, contributors and ingestion providers. Their factories run per scope, after activation.
/// </remarks>
[Experimental("AMEXT001")]
public interface IAgentMemoryModule
{
    /// <summary>Declares the module's options, contributors and ingestion providers.</summary>
    void Configure(IModuleBuilder builder);
}

/// <summary>What a module may declare.</summary>
/// <remarks>
/// Deliberately not <c>IServiceCollection</c>: a module declares capabilities and the host decides lifetimes, order and
/// decoration (spec §4.1). Every identity a module adds must be in its own namespace (<c>moduleId:local</c>); activation
/// refuses any other.
/// </remarks>
[Experimental("AMEXT001")]
public interface IModuleBuilder
{
    /// <summary>The module's own validated manifest.</summary>
    ModuleManifest Manifest { get; }

    /// <summary>
    /// Binds the module's options from its configuration section: the profile entry's <c>configuration</c>, or by default
    /// <c>AgentMemory:Modules:&lt;moduleId&gt;</c>. Bound once at activation, validated by <paramref name="validator"/> and any
    /// <see cref="IValidateOptions{TOptions}"/> the host registered, and never reloaded.
    /// </summary>
    /// <remarks>One options type per module. Values become part of the module set's fingerprint as a hash, so keep secrets
    /// in host services rather than module options.</remarks>
    void Configure<TOptions>(IValidateOptions<TOptions>? validator = null) where TOptions : class, new();

    /// <summary>Adds a context contributor, created once per scope after activation.</summary>
    void AddContextContributor(Func<ModuleActivationContext, IContextContributor> create);

    /// <summary>Adds an ingestion provider, created once per scope after activation.</summary>
    void AddIngestionProvider(Func<ModuleActivationContext, IIngestionProvider> create);
}

/// <summary>
/// A storage adapter for statically composed modules: the capability probe plus the store and module-bound readers it
/// creates. <c>AgentMemory.Extensibility.Neo4j</c> supplies one (<c>UseNeo4jStore</c>).
/// </summary>
[Experimental("AMEXT001")]
public interface IModuleStoreAdapter : IModuleSchemaCapabilityProbe
{
    /// <summary>Creates the store module writes go through, for the active resolution.</summary>
    IModuleStore CreateStore(IServiceProvider services, ModuleResolution resolution);

    /// <summary>Creates a reader bound to one module of the active resolution.</summary>
    Reading.IModuleReader CreateReader(IServiceProvider services, ModuleResolution resolution, string moduleId);
}

/// <summary>Activation failed: the module set did not compose, bind or validate. Nothing is half-active.</summary>
[Experimental("AMEXT001")]
public sealed class ModuleActivationException : InvalidOperationException
{
    /// <summary>Creates the exception with every problem found.</summary>
    public ModuleActivationException(IReadOnlyList<string> errors)
        : base("The module set cannot be activated:" + Environment.NewLine + string.Join(Environment.NewLine, (errors ?? []).Select(e => "- " + e))) =>
        Errors = errors ?? [];

    /// <summary>Every problem found, in ordinal order.</summary>
    public IReadOnlyList<string> Errors { get; }
}
