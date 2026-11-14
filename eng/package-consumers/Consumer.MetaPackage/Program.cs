using AgentMemory;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
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

// AddNeo4jAgentMemory does not register an embedding generator (Core is provider-agnostic);
// ValidateOnBuild requires every registered service's dependency graph to be resolvable.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

using var provider = services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true,
});

Console.WriteLine(typeof(IMemoryService).FullName);

// The migrations an application installed through the meta-package actually gets (embedded in AgentMemory.Neo4j since
// NuGet does not pass a dependency's content files on); eng/verify-packages-install.sh compares this with the source tree.
var migrations = typeof(AgentMemory.Neo4j.Infrastructure.Neo4jOptions).Assembly.GetManifestResourceNames()
    .Count(name => name.StartsWith("AgentMemory.Neo4j.Migrations/", StringComparison.Ordinal) && name.EndsWith(".cypher", StringComparison.Ordinal));
Console.WriteLine($"embedded-migrations={migrations}");
