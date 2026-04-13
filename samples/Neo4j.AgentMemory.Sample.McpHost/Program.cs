using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core;
using Neo4j.AgentMemory.Core.Stubs;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

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

// StubEmbeddingProvider returns deterministic random vectors — replace with a real
// embedding provider (e.g., OpenAI text-embedding-3-small) for production use.
builder.Services.AddSingleton<IEmbeddingProvider, StubEmbeddingProvider>();

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
