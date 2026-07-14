using AgentMemory;
using AgentMemory.SemanticKernel;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

var builder = Kernel.CreateBuilder();
builder.Services.AddLogging();

builder.Services.AddNeo4jAgentMemory(
    configureMemory: _ => { },
    configureNeo4j: neo4j =>
    {
        neo4j.Uri = "bolt://localhost:7687";
        neo4j.Username = "neo4j";
        neo4j.Password = "password";
        neo4j.Database = "neo4j";
    });

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

// AddNeo4jMemoryPlugin requires IMemoryService to already be registered (see its XML doc).
builder.AddNeo4jMemoryPlugin();

using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

Console.WriteLine(typeof(IMemoryService).FullName);
