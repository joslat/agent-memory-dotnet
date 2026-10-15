using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Extensibility.Neo4j;

/// <summary>Inspects runner registration without constructing it or opening a connection.</summary>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleSchemaCapabilityProbe : IModuleSchemaCapabilityProbe
{
    /// <inheritdoc/>
    public string Provider => "neo4j-direct";

    /// <inheritdoc/>
    public IReadOnlyList<string> GetProblems(IServiceProvider services, string? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = serviceKey is null
            ? services.GetService<IServiceProviderIsService>()?.IsService(typeof(INeo4jTransactionRunner)) == true
            : services.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(INeo4jTransactionRunner), serviceKey) == true;
        return registered ? [] : ["INeo4jTransactionRunner is not registered at the selected store key (NAMS does not supply this Neo4j capability)."];
    }
}
