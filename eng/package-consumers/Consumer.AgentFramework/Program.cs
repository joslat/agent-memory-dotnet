using AgentMemory;
using AgentMemory.AgentFramework;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Gate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

services.AddAgentMemoryFramework();

// The retrieval memory router: experimental, so a host opts in by name.
#pragma warning disable AMGATE001
services.AddAgentMemoryGate(gate => gate.Mode = MemoryGateMode.Floor);
#pragma warning restore AMGATE001

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

Console.WriteLine(typeof(IMemoryService).FullName);

// The router wraps the assembler the package registered (resolved in a scope; nothing connects). The Neo4j driver
// it creates is disposed asynchronously, so the scope and the provider are too.
await using (var scope = provider.CreateAsyncScope())
    Console.WriteLine(scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>().GetType().Name);
