using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.McpServer;
using AgentMemory.Neo4j.Infrastructure;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentMemory.McpHost;

/// <summary>
/// The turnkey MCP server: 25 memory tools over stdio or HTTP, configured entirely by environment.
/// </summary>
/// <remarks>
/// <para>
/// The tools already existed and there was <b>no way to run them without writing a .NET host first</b>
/// — which meant the audience was people who would build one anyway, and everyone else could not try
/// the project at all. This is that host, packaged as a global tool so trying it is
/// <c>dotnet tool install -g</c> and one command.
/// </para>
/// <para>
/// <b>stdout belongs to the protocol</b> on the stdio transport. Every diagnostic here goes to stderr,
/// including startup failures: one stray <c>Console.WriteLine</c> corrupts the JSON-RPC stream and
/// presents as an unreadable client-side parse error rather than as the log line it is.
/// </para>
/// </remarks>
internal static class McpHostProgram
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h" or "/?"))
        {
            Console.Error.WriteLine(McpHostOptions.HelpText);
            return 0;
        }

        McpHostOptions options;
        try
        {
            options = McpHostOptions.Parse(args, Environment.GetEnvironmentVariable);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"agent-memory-mcp: {ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(McpHostOptions.HelpText);
            return 2;
        }

        var builder = WebApplication.CreateSlimBuilder(args);

        // stdout is the protocol on stdio, and there is no reason for a server's own logs to differ by
        // transport, so both go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(options.LogLevel);

        builder.Services.AddNeo4jAgentMemory(neo4j =>
        {
            neo4j.Uri = options.Neo4jUri;
            neo4j.Username = options.Neo4jUsername;
            neo4j.Password = options.Neo4jPassword;
            neo4j.Database = options.Neo4jDatabase;
        });
        // 25.4. Was `AddAgentMemoryCore(_ => { })` -- an empty configure lambda, so every MCP server
        // ran on stock memory defaults with no way for an operator to change recall depth, similarity
        // threshold, reranking or temporal resolution. The empty body was not laziness: until 25.1 made
        // the scalar options settable, no other body would have compiled.
        builder.Services.AddAgentMemoryCore(options.Memory);
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            new AzureOpenAIClient(new Uri(options.AzureEndpoint), new AzureKeyCredential(options.AzureApiKey))
                .GetEmbeddingClient(options.EmbeddingDeployment)
                .AsIEmbeddingGenerator());

        var mcp = builder.Services.AddMcpServer();
        if (options.Transport == McpHostTransport.Http) mcp.WithHttpTransport();
        else mcp.WithStdioServerTransport();

        mcp.AddAgentMemoryMcpTools(mcpOptions =>
            {
                mcpOptions.ServerName = options.ServerName;
                mcpOptions.ServerVersion = ThisAssembly.Version;
                mcpOptions.EnableGraphQuery = options.EnableGraphQuery;
                mcpOptions.ReadOnly = options.ReadOnly;
            })
            .AddAgentMemoryMcpPrompts()
            .AddAgentMemoryMcpResources();

        var app = builder.Build();

        if (options.Bootstrap && !await TryBootstrapAsync(app.Services).ConfigureAwait(false))
            return 1;

        Console.Error.WriteLine(
            $"agent-memory-mcp {ThisAssembly.Version}: {options.Transport.ToString().ToLowerInvariant()} transport, "
            + $"{(options.ReadOnly ? "READ-ONLY" : "read-write")}, "
            + $"graph_query {(options.EnableGraphQuery ? "enabled" : "disabled")}, "
            + $"neo4j {options.Neo4jUri}/{options.Neo4jDatabase}.");

        if (options.Transport == McpHostTransport.Http)
        {
            app.MapMcp();
            app.Urls.Clear();
            app.Urls.Add(options.HttpUrl);
        }

        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Applies the schema migrations before serving, and reports a failure as a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every tool needs the indexes and constraints. Without this the first honest experience of the
    /// project is a working server whose searches return nothing — a missing vector index produces
    /// empty results, not an error, so it reads as "the memory is empty" rather than "the schema was
    /// never created".
    /// </para>
    /// <para>
    /// Failing here exits non-zero rather than degrading: an operator who asked for bootstrap and did
    /// not get it must not be left with a server that looks healthy. The message says how to skip it,
    /// because a read-only database user legitimately cannot create indexes.
    /// </para>
    /// </remarks>
    private static async Task<bool> TryBootstrapAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ISchemaBootstrapper>()
                .BootstrapAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"agent-memory-mcp: schema bootstrap failed: {ex.Message}\n"
                + "The server would start but every search would return empty results, because a "
                + "missing vector index yields no rows rather than an error. Fix the connection or "
                + "the database user's privileges, or pass --no-bootstrap if the schema is already "
                + "in place and this user cannot create indexes.");
            return false;
        }
    }
}
