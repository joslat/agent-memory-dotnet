using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.McpServer;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

// This host calls a REAL Azure OpenAI embedding model — no mocks. Requires:
//   ONE inference provider, auto-detected: Azure OpenAI, Bitdeer, OpenAI, Foundry, or any
//   OpenAI-compatible host. The shortest is BITDEER_API_KEY, which gets chat and embeddings.
//   An existing AZURE_OPENAI_ENDPOINT/_API_KEY/_DEPLOYMENT setup still works unchanged.
//   Name one explicitly with AI_INFERENCE_PROVIDER; see docs/configuration/inference-providers.md.
if (!RealModel.TryCreate(out var chatClient, out var embeddingGenerator, out var modelSettings))
{
    // stdout is reserved for the MCP JSON-RPC stream — the message must go to stderr.
    RealModel.PrintMissingProvider("AgentMemory MCP Host", Console.Error);
    return;
}

RealModel.PrintModelBanner(modelSettings);

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr (stdout is used for MCP stdio transport)
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register Neo4j infrastructure
builder.Services.AddNeo4jAgentMemory(neo4j =>
{
    neo4j.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    neo4j.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    neo4j.Password = builder.Configuration["Neo4j:Password"] ?? "password";
    neo4j.Database = builder.Configuration["Neo4j:Database"] ?? "neo4j";

    // THE STORE DIMENSION COMES FROM THE RESOLVED MODEL, not from the Neo4j default. This sample
    // was missed by the pass that added the line everywhere else, because it names its lambda
    // `neo4j` rather than `options` -- a 1024-wide model against the 1536 default builds a vector
    // index that cannot match its own writes, and nothing errors.
    neo4j.EmbeddingDimensions = modelSettings.EmbeddingDimensions!.Value;
});

// Register core memory services
builder.Services.AddAgentMemoryCore(_ => { });

// Provide default IClock and IIdGenerator implementations.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    embeddingGenerator);

// Configure MCP server with stdio transport and all memory tools
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .AddAgentMemoryMcpTools(options =>
    {
        options.ServerName = "neo4j-agent-memory";
        options.ServerVersion = "1.0.0";
        options.EnableGraphQuery = bool.Parse(
            builder.Configuration["McpServer:EnableGraphQuery"] ?? "false");
    });

await builder.Build().RunAsync();
