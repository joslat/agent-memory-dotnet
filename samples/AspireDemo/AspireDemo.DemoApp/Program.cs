using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
    options.Database = builder.Configuration["Neo4j:Database"] ?? "neo4j";
});

builder.Services.AddAgentMemoryCore(_ => { });
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();
builder.Services.AddSingleton<IGraphRagContextSource, DisabledGraphRagContextSource>();
builder.Services.AddSingleton<IMemoryExtractionPipeline, DisabledMemoryExtractionPipeline>();

using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();

var services = scope.ServiceProvider;
var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AspireDemo");

logger.LogInformation("=== Aspire Demo — Agent Memory scripted run ===");
logger.LogInformation("Bootstrapping Neo4j schema...");
await services.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

logger.LogInformation("Seeding deterministic demo data...");
await DemoDataSeeder.SeedAsync(services, logger);

logger.LogInformation("Running scripted recall/context demo...");
await ScriptedDemo.RunAsync(services, logger);

logger.LogInformation("Neo4j Browser: http://localhost:7474");
logger.LogInformation("=== Aspire Demo complete ===");
