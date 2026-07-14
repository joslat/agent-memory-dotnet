using AgentMemory;
using AgentMemory.McpServer;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var services = new ServiceCollection();
services.AddLogging();

services.AddNeo4jAgentMemory(
    configureMemory: _ => { },
    configureNeo4j: neo4j =>
    {
        neo4j.Uri = "bolt://localhost:7687";
        neo4j.Username = "neo4j";
        neo4j.Password = "password";
        neo4j.Database = "neo4j";
    });

services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

services.AddMcpServer().AddAgentMemoryMcpTools();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

Console.WriteLine(typeof(IMemoryService).FullName);
