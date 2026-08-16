using System.Globalization;
using AgentMemory.Abstractions.Options;
using AgentMemory.McpServer;
using Microsoft.Extensions.Logging;

namespace AgentMemory.McpHost;

/// <summary>Which transport the server speaks.</summary>
internal enum McpHostTransport
{
    /// <summary>JSON-RPC over stdin/stdout — what a desktop MCP client launches.</summary>
    Stdio = 0,

    /// <summary>Streamable HTTP — one server, many clients, reachable from a container.</summary>
    Http = 1,
}

/// <summary>
/// Everything the host needs, resolved from environment variables with command-line overrides.
/// </summary>
/// <remarks>
/// <para>
/// <b>Environment first.</b> A desktop MCP client launches the server through a config file whose
/// <c>env</c> block is the natural place for credentials; a container gets them from the orchestrator.
/// Flags exist so the same binary can be driven by hand without exporting anything, and they win when
/// both are present, because a flag is the more specific and more deliberate statement.
/// </para>
/// <para>
/// Parsing is separated from running so it is testable without a database, a provider or a port — the
/// defaults and the precedence rules are the part most likely to be quietly wrong, and the part a
/// runtime smoke test would never distinguish from a working server.
/// </para>
/// </remarks>
internal sealed record McpHostOptions
{
    internal required string AzureEndpoint { get; init; }
    internal required string AzureApiKey { get; init; }
    internal required string EmbeddingDeployment { get; init; }

    internal required string Neo4jUri { get; init; }
    internal required string Neo4jUsername { get; init; }
    internal required string Neo4jPassword { get; init; }
    internal required string Neo4jDatabase { get; init; }

    internal McpHostTransport Transport { get; init; }
    internal string HttpUrl { get; init; } = "http://localhost:5233";
    internal string ServerName { get; init; } = "agent-memory";
    internal bool ReadOnly { get; init; }
    internal bool EnableGraphQuery { get; init; }
    internal bool Bootstrap { get; init; } = true;
    internal LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary>
    /// Memory behaviour for this server (25.4). Defaults are <see cref="MemoryOptions"/>'s own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host previously registered memory with <c>AddAgentMemoryCore(_ => { })</c> — an empty
    /// configure lambda — so every MCP server ran on stock defaults with no way to change them. That
    /// was not quite an oversight: until 25.1 made the scalar options settable, a configure lambda
    /// <i>could not</i> assign any of them, so the empty body was the only body that compiled.
    /// </para>
    /// <para>
    /// Environment-only, deliberately. These are deployment tuning rather than per-invocation choices,
    /// and the flag surface is the part an operator reads under time pressure — it stays about
    /// transport and safety.
    /// </para>
    /// </remarks>
    internal MemoryOptions Memory { get; init; } = new();

    private static readonly string[] Known =
    [
        "--transport", "--url", "--server-name", "--read-only", "--enable-graph-query",
        "--no-bootstrap", "--log-level", "--help", "-h", "/?",
    ];

    internal static McpHostOptions Parse(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        // An unknown flag is an error, never ignored. A typo in --read-only would otherwise start a
        // fully writable server that the operator believes is read-only -- the one failure this host
        // must not have.
        foreach (var argument in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
            if (!Known.Contains(argument, StringComparer.Ordinal))
                throw new ArgumentException($"unknown option '{argument}'.");

        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }
        bool Flag(string name) => Array.IndexOf(args, name) >= 0;

        string Required(string variable, string what) =>
            environment(variable) is { Length: > 0 } value
                ? value
                : throw new ArgumentException(
                    $"{variable} is required ({what}). Set it in the environment, or in the `env` block "
                    + "of your MCP client's server configuration.");

        return new McpHostOptions
        {
            AzureEndpoint = Required("AZURE_OPENAI_ENDPOINT", "e.g. https://<resource>.openai.azure.com/"),
            AzureApiKey = Required("AZURE_OPENAI_API_KEY", "the embedding provider key"),
            EmbeddingDeployment = environment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") is { Length: > 0 } d
                ? d
                : "text-embedding-3-small",

            Neo4jUri = environment("NEO4J_URI") is { Length: > 0 } uri ? uri : "bolt://localhost:7687",
            Neo4jUsername = environment("NEO4J_USERNAME") is { Length: > 0 } u ? u : "neo4j",
            // No default. A blank password silently becomes an authentication failure at the first
            // query, which reads as "the database is empty" from an MCP client.
            Neo4jPassword = Required("NEO4J_PASSWORD", "the Neo4j password"),
            Neo4jDatabase = environment("NEO4J_DATABASE") is { Length: > 0 } db ? db : "neo4j",

            Transport = ParseTransport(Value("--transport") ?? environment("AGENT_MEMORY_MCP_TRANSPORT")),
            HttpUrl = FirstNonEmpty(Value("--url"), environment("AGENT_MEMORY_MCP_URL"))
                ?? "http://localhost:5233",
            ServerName = FirstNonEmpty(Value("--server-name"), environment("AGENT_MEMORY_MCP_SERVER_NAME"))
                ?? "agent-memory",

            // Flag OR environment, never flag-overrides-environment: for a safety switch the union is
            // the correct combination. Someone who set the variable and someone who passed the flag
            // both asked for read-only, and neither can be cancelled by the other's absence.
            ReadOnly = Flag("--read-only") || Boolean(environment("AGENT_MEMORY_MCP_READ_ONLY")),

            EnableGraphQuery = Flag("--enable-graph-query")
                || Boolean(environment("AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY")),
            Bootstrap = !Flag("--no-bootstrap") && !Boolean(environment("AGENT_MEMORY_MCP_NO_BOOTSTRAP")),
            LogLevel = ParseLogLevel(Value("--log-level") ?? environment("AGENT_MEMORY_MCP_LOG_LEVEL")),
            Memory = ParseMemory(environment),
        };
    }

    /// <summary>
    /// Memory options from the environment, falling back to the library defaults for anything unset.
    /// </summary>
    /// <remarks>
    /// <c>Recall</c> is replaced wholesale with a <c>with</c> expression rather than mutated: it
    /// defaults to <c>RecallOptions.Default</c>, which is a single instance shared by the whole
    /// process, so assigning through it would change the default for every other consumer in the host.
    /// </remarks>
    private static MemoryOptions ParseMemory(Func<string, string?> environment)
    {
        var recall = RecallOptions.Default with
        {
            MaxFacts = Integer(environment("AGENT_MEMORY_MCP_RECALL_FACTS"), RecallOptions.Default.MaxFacts),
            MaxEntities = Integer(environment("AGENT_MEMORY_MCP_RECALL_ENTITIES"), RecallOptions.Default.MaxEntities),
            MaxPreferences = Integer(environment("AGENT_MEMORY_MCP_RECALL_PREFERENCES"), RecallOptions.Default.MaxPreferences),
            MaxRelevantMessages = Integer(environment("AGENT_MEMORY_MCP_RECALL_MESSAGES"), RecallOptions.Default.MaxRelevantMessages),
            MinSimilarityScore = Fraction(environment("AGENT_MEMORY_MCP_MIN_SIMILARITY"), RecallOptions.Default.MinSimilarityScore),
        };

        return new MemoryOptions
        {
            Recall = recall,
            NodeDistanceReranking = Boolean(environment("AGENT_MEMORY_MCP_RERANK_NODE_DISTANCE")),
            MentionFrequencyReranking = Boolean(environment("AGENT_MEMORY_MCP_RERANK_MENTION_FREQUENCY")),
            ResolveTemporalQueries = Boolean(environment("AGENT_MEMORY_MCP_RESOLVE_TEMPORAL")),
        };
    }

    /// <summary>A non-negative integer, or the default when unset. A malformed value is an error.</summary>
    /// <remarks>
    /// Never silently falls back on garbage: an operator who set <c>RECALL_FACTS=ten</c> asked for
    /// something, and starting on the default would hand them a quietly under-configured server.
    /// </remarks>
    private static int Integer(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            throw new ArgumentException($"'{value}' is not a non-negative integer.");
        return parsed;
    }

    private static double Fraction(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 0 or > 1)
        {
            throw new ArgumentException($"'{value}' is not a similarity score between 0 and 1.");
        }

        return parsed;
    }

    /// <summary>The first value that is present and not blank, or null.</summary>
    /// <remarks>
    /// Blank is treated as absent throughout. An exported-but-empty variable is how a container
    /// silently ends up binding to "" and failing at startup with a message about a URL nobody wrote.
    /// </remarks>
    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    private static bool Boolean(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static McpHostTransport ParseTransport(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "" or "stdio" => McpHostTransport.Stdio,
            "http" => McpHostTransport.Http,
            _ => throw new ArgumentException("--transport must be one of: stdio, http."),
        };

    private static LogLevel ParseLogLevel(string? value) =>
        value is null or "" ? LogLevel.Information
        : Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level
        : throw new ArgumentException(
            "--log-level must be one of: trace, debug, information, warning, error, critical, none.");

    internal static string HelpText => string.Format(
        CultureInfo.InvariantCulture,
        """
        agent-memory-mcp - MCP server for AgentMemory for .NET

          agent-memory-mcp [--transport stdio|http] [--url http://localhost:5233]
                           [--read-only] [--enable-graph-query] [--no-bootstrap]
                           [--server-name agent-memory] [--log-level information]

        Required environment:
          AZURE_OPENAI_ENDPOINT              https://<resource>.openai.azure.com/
          AZURE_OPENAI_API_KEY               embedding provider key
          NEO4J_PASSWORD                     Neo4j password

        Optional environment (defaults shown):
          AZURE_OPENAI_EMBEDDING_DEPLOYMENT  text-embedding-3-small
          NEO4J_URI                          bolt://localhost:7687
          NEO4J_USERNAME                     neo4j
          NEO4J_DATABASE                     neo4j
          AGENT_MEMORY_MCP_TRANSPORT         stdio
          AGENT_MEMORY_MCP_URL               http://localhost:5233
          AGENT_MEMORY_MCP_READ_ONLY         unset
          AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY unset
          AGENT_MEMORY_MCP_NO_BOOTSTRAP      unset
          AGENT_MEMORY_MCP_LOG_LEVEL         information

        Memory tuning (environment only; defaults are the library's):
          AGENT_MEMORY_MCP_RECALL_FACTS      10
          AGENT_MEMORY_MCP_RECALL_ENTITIES   10
          AGENT_MEMORY_MCP_RECALL_PREFERENCES 5
          AGENT_MEMORY_MCP_RECALL_MESSAGES   5
          AGENT_MEMORY_MCP_MIN_SIMILARITY    0.7
          AGENT_MEMORY_MCP_RERANK_NODE_DISTANCE      unset
          AGENT_MEMORY_MCP_RERANK_MENTION_FREQUENCY  unset
          AGENT_MEMORY_MCP_RESOLVE_TEMPORAL          unset

        --read-only removes every tool that writes from the server's tool list entirely, rather than
        refusing them when called: a tool a client can see is one a model will try. {0} of the {1}
        tools remain.

        --enable-graph-query exposes graph_query, which runs arbitrary Cypher. It is off by default and
        is withheld under --read-only regardless, because a mode guaranteeing "nothing changes" must
        not rest on a query parser.

        Schema migrations run at startup unless --no-bootstrap. A missing vector index returns no rows
        rather than an error, so a server started without them looks healthy and answers nothing.

        Logs go to stderr on both transports; stdout is the JSON-RPC stream on stdio.
        """,
        McpToolAccess.ReadTools.Count,
        McpToolAccess.ReadTools.Count + McpToolAccess.WriteTools.Count);
}

/// <summary>The version this build reports in the MCP handshake.</summary>
/// <remarks>
/// Read from the assembly rather than written as a literal: a hand-maintained version string in a
/// handshake is one that stops matching the package it was installed from, and the handshake is where
/// a client reports what it is talking to.
/// </remarks>
internal static class ThisAssembly
{
    internal static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
