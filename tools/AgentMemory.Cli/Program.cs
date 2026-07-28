using AgentMemory;                          // meta AddNeo4jAgentMemory
using AgentMemory.Core.Stubs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli;
using AgentMemory.Cli.Commands;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var cli = CliArgs.Parse(args);

if (cli.Command is null || string.Equals(cli.Command, "help", StringComparison.OrdinalIgnoreCase))
{
    CliHelp.Print(Console.Out);
    return cli.Command is null ? 1 : 0;
}

var known = new[] { "migrate", "bootstrap", "consolidate", "decay", "conflicts", "schema-parity", "schema-check", "invalidate", "supersede", "history", "evaluate", "perf" };
if (!known.Contains(cli.Command, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"error: unknown command '{cli.Command}'.");
    CliHelp.Print(Console.Out);
    return 1;
}

// schema-parity is pure static analysis of embedded snapshots — no Neo4j connection or host needed.
if (string.Equals(cli.Command, "schema-parity", StringComparison.OrdinalIgnoreCase))
{
    return new AgentMemory.Cli.Commands.SchemaParityCommand(Console.Out).Execute(cli.Get("upstream-version"));
}

// perf provisions its OWN Neo4j (Testcontainers) and its own deterministic embedding/model stand-ins,
// so it must not go through the shared host below — that one binds to a caller-supplied connection and
// would measure whatever database happens to be configured, with whatever data is already in it.
if (string.Equals(cli.Command, "perf", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        if (string.Equals(cli.Subcommand, "baseline", StringComparison.OrdinalIgnoreCase))
        {
            return await new AgentMemory.Cli.Commands.PerfBaselineCommand(Console.Out).ExecuteAsync(
                cli.HasFlag("update") ? cli.Get("update") ?? bool.TrueString : null,
                cli.Get("report"),
                cli.Get("output"));
        }

        if (string.Equals(cli.Subcommand, "gate", StringComparison.OrdinalIgnoreCase))
        {
            return await new AgentMemory.Cli.Commands.PerfGateCommand(Console.Out).ExecuteAsync(
                cli.Get("baseline"),
                cli.Get("report"),
                cli.Get("allow-counter-change"),
                cli.Get("counter-change-justification"));
        }

        if (string.Equals(cli.Subcommand, "ab", StringComparison.OrdinalIgnoreCase))
        {
            return await new AgentMemory.Cli.Commands.PerfAbCommand(Console.Out).ExecuteAsync(
                cli.Get("control"),
                cli.Get("candidate"),
                cli.Get("scenarios"),
                cli.Get("iterations"),
                cli.Get("warmup"),
                cli.Get("embedding-dimensions"),
                cli.Get("latency"),
                cli.Get("output"));
        }

        if (string.Equals(cli.Subcommand, "ledger", StringComparison.OrdinalIgnoreCase))
        {
            if (cli.Positionals.Count < 2 ||
                !string.Equals(cli.Positionals[1], "add", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("error: perf ledger requires the 'add' operation.");
                return 1;
            }

            return await new AgentMemory.Cli.Commands.PerfLedgerCommand(Console.Out).ExecuteAsync(
                cli.Get("run"),
                cli.Get("compared-to"),
                cli.Get("verdict"),
                cli.Get("ledger"));
        }

        if (string.Equals(cli.Subcommand, "cold", StringComparison.OrdinalIgnoreCase))
        {
            return await new AgentMemory.Cli.Commands.PerfColdCommand(Console.Out).ExecuteAsync(
                cli.Get("label"),
                cli.Get("scenarios"),
                cli.Get("samples"),
                cli.Get("warmup"),
                cli.Get("embedding-dimensions"),
                cli.Get("scale"),
                cli.Get("latency"),
                cli.Get("output"));
        }

        if (string.Equals(cli.Subcommand, "concurrency", StringComparison.OrdinalIgnoreCase))
        {
            return await new AgentMemory.Cli.Commands.PerfConcurrencyCommand(Console.Out).ExecuteAsync(
                cli.Get("label"),
                cli.Get("levels"),
                cli.Get("pool-size"),
                cli.Get("embedding-dimensions"),
                cli.Get("output"));
        }


        if (cli.Subcommand is not null &&
            !string.Equals(cli.Subcommand, "run", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"error: unknown perf subcommand '{cli.Subcommand}'. Use 'run', 'cold', 'concurrency', 'ab', 'ledger', 'baseline', or 'gate'.");
            return 1;
        }

        return await new AgentMemory.Cli.Commands.PerfCommand(Console.Out).ExecuteAsync(
            cli.Get("label"),
            cli.Get("scenarios"),
            cli.Get("iterations"),
            cli.Get("warmup"),
            cli.Get("embedding-dimensions"),
            cli.Get("scale"),
            cli.Get("latency"),
            cli.Get("output"),
            cli.Get("quality-gate"),
            cli.HasFlag("single-shot")
                ? cli.Get("single-shot") ?? bool.TrueString
                : null);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

try
{
    // No args passed to the builder: configuration comes from environment variables + appsettings, so a
    // bare command token (e.g. "migrate") never trips the configuration command-line provider.
    var builder = Host.CreateApplicationBuilder();
    var cfg = builder.Configuration;

    // Precedence per setting: CLI option > Neo4j:* config > NEO4J_* env > default.
    string Resolve(string option, string defaultValue, string cfgKey, string envKey)
    {
        var overrideValue = cli.Get(option);
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue;
        return cfg[cfgKey] ?? cfg[envKey] ?? defaultValue;
    }

    var uri = Resolve("uri", "bolt://localhost:7687", "Neo4j:Uri", "NEO4J_URI");
    var user = Resolve("user", "neo4j", "Neo4j:Username", "NEO4J_USERNAME");
    var password = Resolve("password", "neo4j", "Neo4j:Password", "NEO4J_PASSWORD");
    var database = Resolve("database", "neo4j", "Neo4j:Database", "NEO4J_DATABASE");
    var dims = int.TryParse(
        Resolve("embedding-dimensions", "1536", "Neo4j:EmbeddingDimensions", "NEO4J_EMBEDDING_DIMENSIONS"),
        out var parsed) ? parsed : 1536;

    builder.Services.AddNeo4jAgentMemory(
        _ => { },
        o =>
        {
            o.Uri = uri;
            o.Username = user;
            o.Password = password;
            o.Database = database;
            o.EmbeddingDimensions = dims;
        });
    builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        new StubEmbeddingGenerator(
            sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
            dims));

    // Must dispose the host via DisposeAsync, not the synchronous Dispose(): the Neo4j driver factory is
    // registered as an IAsyncDisposable-ONLY singleton on the root provider, and a synchronous
    // ServiceProvider.Dispose() over an async-only disposable THROWS InvalidOperationException — which the
    // catch below would turn every successful command into a spurious "error: ..." + exit code 1.
    // IHost (the static type) only exposes IDisposable, but the generic-host implementation is
    // IAsyncDisposable, so cast for the `await using`.
    var host = builder.Build();
    await using var hostAsync = (IAsyncDisposable)host;
    await using var scope = host.Services.CreateAsyncScope();
    var sp = scope.ServiceProvider;
    var output = Console.Out;

    return cli.Command.ToLowerInvariant() switch
    {
        "migrate" => await new MigrateCommand(
            sp.GetRequiredService<IMigrationRunner>(), output).ExecuteAsync(),
        "bootstrap" => await new BootstrapCommand(
            sp.GetRequiredService<ISchemaBootstrapper>(), output).ExecuteAsync(),
        "schema-check" => await new SchemaCheckCommand(
            sp.GetRequiredService<INeo4jTransactionRunner>(),
            sp.GetRequiredService<IOptions<Neo4jOptions>>(), output).ExecuteAsync(),
        "consolidate" => await new ConsolidateCommand(
            sp.GetRequiredService<IConsolidationService>(), output).ExecuteAsync(cli.HasFlag("apply")),
        "decay" => await new DecayCommand(
            sp.GetRequiredService<IMemoryDecayService>(), output).ExecuteAsync(cli.Get("owner")),
        "conflicts" => await new ConflictsCommand(
            sp.GetRequiredService<IConflictDetectionService>(), output).ExecuteAsync(),
        "invalidate" => await new InvalidateCommand(
            sp.GetRequiredService<IFactRepository>(),
            sp.GetRequiredService<IEntityRepository>(),
            sp.GetRequiredService<IPreferenceRepository>(), output)
            .ExecuteAsync(cli.Get("type"), cli.Get("id"), cli.Get("owner")),
        "supersede" => await new SupersedeCommand(
            sp.GetRequiredService<IFactRepository>(),
            sp.GetRequiredService<IPreferenceRepository>(), output)
            .ExecuteAsync(cli.Get("type"), cli.Get("loser"), cli.Get("winner"), cli.Get("owner")),
        "history" => await new HistoryCommand(
            sp.GetRequiredService<IMemoryHistoryService>(), output)
            .ExecuteAsync(
                cli.Get("type"), cli.Get("id"), cli.Get("owner"),
                liveOnly: cli.HasFlag("live-only"),
                ownOnly: cli.HasFlag("own-only"),
                limitValue: cli.Get("limit")),
        "evaluate" => await new EvaluationCommand(
            sp.GetRequiredService<ISchemaBootstrapper>(),
            sp.GetRequiredService<INeo4jTransactionRunner>(),
            sp.GetRequiredService<IOptions<Neo4jOptions>>(),
            sp.GetRequiredService<IShortTermMemoryService>(),
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IReasoningMemoryService>(),
            sp.GetRequiredService<IMemoryHistoryService>(),
            sp.GetRequiredService<IToolCallRepository>(),
            output)
            .ExecuteAsync(cli.Get("output"), cli.Get("iterations"), cli.Get("owner")),
        _ => 1,
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
