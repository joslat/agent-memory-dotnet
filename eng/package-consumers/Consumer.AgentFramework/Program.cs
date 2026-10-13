using AgentMemory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using AgentMemory.AgentFramework;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Gate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var services = new ServiceCollection();
services.AddLogging();
// The package check validates DI and must never send inference requests.
services.AddSingleton<IChatClient, NoInferenceChatClient>();

services.AddNeo4jAgentMemory(
    MemoryOptions.CreateRecommended(),
    configureNeo4j: neo4j =>
    {
        neo4j.Uri = "bolt://localhost:7687";
        neo4j.Username = "neo4j";
        neo4j.Password = "password";
        neo4j.Database = "neo4j";
    },
    configureLlm: llm => llm.ApplyRecommended());

services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

services.AddAgentMemoryFramework(adapter => adapter.ApplyRecommended());

// Supported winner APIs compile without diagnostic suppression; no endpoint is contacted.
services.AddAgentMemoryGate(gate =>
{
    gate.ApplyRecommended();
    gate.Mode = MemoryGateMode.Floor;
});

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

Console.WriteLine(typeof(IMemoryService).FullName);

// The router wraps the assembler the package registered (resolved in a scope; nothing connects). The Neo4j driver
// it creates is disposed asynchronously, so the scope and the provider are too.
await using (var scope = provider.CreateAsyncScope())
{
    Console.WriteLine(scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>().GetType().Name);
    new ExtensionProbe(scope.ServiceProvider).Verify();
}
