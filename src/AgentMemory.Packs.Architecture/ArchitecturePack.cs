// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Packs.Architecture;

/// <summary>
/// The architecture memory pack (coding packs CMP-5): a project's systems, the components that make them up, the APIs they
/// provide and consume with their operations, the packages they depend on and who owns each, read from the project's own
/// files (Backstage catalogs, OpenAPI and AsyncAPI documents, CycloneDX and SPDX SBOMs, Structurizr workspaces) through a
/// deterministic reader and a snapshot import; no model writes them. A walk starts from the work in hand: an API's document,
/// an operation's id named in code, or a component's source folder; and moves to what provides, consumes, owns or contains it.
/// </summary>
[Experimental("AMEXT001")]
public sealed class ArchitecturePack : IAgentMemoryModule
{
    /// <summary>The module identity.</summary>
    public const string Id = "io.agentmemory.packs.architecture";
    /// <summary>A software system: what a team ships and others depend on.</summary>
    public const string SystemType = "ArchitectureSystem";
    /// <summary>A deployable or buildable part of a system: a service, a website, a library, a database.</summary>
    public const string ComponentType = "ArchitectureComponent";
    /// <summary>An interface a component provides: OpenAPI, AsyncAPI, gRPC, GraphQL.</summary>
    public const string ApiType = "ArchitectureApi";
    /// <summary>One operation of an API: an HTTP method on a path, or a message on a channel.</summary>
    public const string OperationType = "ArchitectureOperation";
    /// <summary>A package a component depends on, from an SBOM.</summary>
    public const string DependencyType = "ArchitectureDependency";
    /// <summary>A team or a person that owns something.</summary>
    public const string OwnerType = "ArchitectureOwner";

    /// <summary>Component belongs to a system.</summary>
    public const string PartOf = "ARCHITECTURE_PART_OF";
    /// <summary>Component provides an API.</summary>
    public const string Provides = "ARCHITECTURE_PROVIDES";
    /// <summary>Component consumes an API.</summary>
    public const string Consumes = "ARCHITECTURE_CONSUMES";
    /// <summary>Component depends on another component.</summary>
    public const string DependsOn = "ARCHITECTURE_DEPENDS_ON";
    /// <summary>Operation belongs to an API.</summary>
    public const string OperationOf = "ARCHITECTURE_OPERATION_OF";
    /// <summary>API belongs to a system.</summary>
    public const string ApiOf = "ARCHITECTURE_API_OF";
    /// <summary>Component uses a package.</summary>
    public const string UsesPackage = "ARCHITECTURE_USES_PACKAGE";
    /// <summary>System is owned by an owner.</summary>
    public const string SystemOwnedBy = "ARCHITECTURE_SYSTEM_OWNED_BY";
    /// <summary>Component is owned by an owner.</summary>
    public const string ComponentOwnedBy = "ARCHITECTURE_COMPONENT_OWNED_BY";
    /// <summary>API is owned by an owner.</summary>
    public const string ApiOwnedBy = "ARCHITECTURE_API_OWNED_BY";

    private static readonly Lazy<ModuleManifest> Metadata = new(() => ModuleManifest.Load(typeof(ArchitecturePack).Assembly));

    /// <summary>The embedded manifest.</summary>
    public static ModuleManifest Manifest => Metadata.Value;

    /// <inheritdoc/>
    public void Configure(IModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var view in new[] { "components", "apis", "operations" })
            builder.AddContextContributor(c => new ViewContributor(c.Resolution, c.Reader, view));
    }
}
