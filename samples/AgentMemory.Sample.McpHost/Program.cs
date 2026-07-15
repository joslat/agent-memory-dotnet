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
//   AZURE_OPENAI_ENDPOINT               (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY                (required — no offline-stub fallback)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (embedding deployment name; default: text-embedding-ada-002)
if (!RealAzureOpenAI.TryCreate(out var azureClient, out _, out var embeddingDeployment))
{
    // stdout is reserved for the MCP JSON-RPC stream — the message must go to stderr.
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory MCP Host", Console.Error);
    return;
}

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
});

// Register core memory services
builder.Services.AddAgentMemoryCore(_ => { });

// Provide default IClock and IIdGenerator implementations.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    azureClient.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator());

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
