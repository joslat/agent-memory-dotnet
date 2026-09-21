using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

// This demo calls a REAL Azure OpenAI embedding model — no mocks. Requires:
//   AZURE_OPENAI_ENDPOINT               (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY                (required — no offline-stub fallback)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (embedding deployment name; default: text-embedding-ada-002)
if (!RealModel.TryCreate(out var chatClient, out var embeddingGenerator, out var modelSettings))
{
    RealModel.PrintMissingProvider("Aspire Demo — Agent Memory scripted run");
    return;
}

RealModel.PrintModelBanner(modelSettings);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
    // THE STORE DIMENSION COMES FROM THE RESOLVED MODEL, not from the Neo4j default. Every
    // sample used to run on Azure's text-embedding-ada-002 at 1536, which is also that default,
    // so nobody had to say it. Bitdeer's default embedding model is 1024-wide: leaving this unset
    // builds a vector index that does not match what writes into it, and nothing says so.
    options.EmbeddingDimensions = modelSettings.EmbeddingDimensions!.Value;
    options.Database = builder.Configuration["Neo4j:Database"] ?? "neo4j";
});

builder.Services.AddAgentMemoryCore(_ => { });
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    embeddingGenerator);
builder.Services.AddSingleton<IGraphRagContextSource, DisabledGraphRagContextSource>();
builder.Services.AddSingleton<IMemoryExtractionPipeline, DisabledMemoryExtractionPipeline>();

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the async-only Neo4j driver factory on exit
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
