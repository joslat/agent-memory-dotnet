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

var known = new[] { "migrate", "bootstrap", "retrim", "consolidate", "decay", "conflicts", "schema-parity", "schema-check", "invalidate", "supersede", "history", "evaluate", "perf", "block", "routing-score", "integrity", "owner", "modules", "packs", "codemap", "decisions", "work" };
if (!known.Contains(cli.Command, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"error: unknown command '{cli.Command}'.");
    CliHelp.Print(Console.Out);
    return 1;
}

// routing-score reads a frozen routing set and scores a policy on it: no store, no model (40.61).
if (string.Equals(cli.Command, "routing-score", StringComparison.OrdinalIgnoreCase))
{
    return new AgentMemory.Cli.Commands.RoutingScoreCommand(Console.Out)
        .Execute(cli.Get("set"), cli.Get("split"), cli.Get("policy"), cli.HasFlag("misses"));
}

// codemap scan reads a repository with Roslyn offline; codemap import goes through the host below (CMP-2a).
if (string.Equals(cli.Command, "codemap", StringComparison.OrdinalIgnoreCase) && !string.Equals(cli.Subcommand, "import", StringComparison.OrdinalIgnoreCase))
{
    if (string.Equals(cli.Subcommand, "pack", StringComparison.OrdinalIgnoreCase))
        return new AgentMemory.Cli.Commands.CodeMapCommand(Console.Out)
            .Pack(cli.Get("root"), cli.Get("scip"), cli.Get("id"), cli.Get("version"), cli.Get("key"), cli.Get("out"), cli.Get("repository"));
    if (!string.Equals(cli.Subcommand, "scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("error: codemap needs scan, pack or import.");
        return 2;
    }
    return new AgentMemory.Cli.Commands.CodeMapCommand(Console.Out).Scan(cli.Get("root"), cli.Get("output"), cli.Get("scip"));
}

// decisions scan reads ADR files and Squad's log offline; decisions import goes through the host below (CMP-3).
if (string.Equals(cli.Command, "decisions", StringComparison.OrdinalIgnoreCase) && !string.Equals(cli.Subcommand, "import", StringComparison.OrdinalIgnoreCase))
{
    if (!string.Equals(cli.Subcommand, "scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("error: decisions needs scan or import.");
        return 2;
    }
    return new AgentMemory.Cli.Commands.DecisionsCommand(Console.Out).Scan(cli.Get("root"));
}

// work scan reads a task ledger and the repository's commits offline; work import goes through the host below (CODING-hierarchy).
if (string.Equals(cli.Command, "work", StringComparison.OrdinalIgnoreCase) && !string.Equals(cli.Subcommand, "import", StringComparison.OrdinalIgnoreCase))
{
    if (!string.Equals(cli.Subcommand, "scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("error: work needs scan or import.");
        return 2;
    }
    return new AgentMemory.Cli.Commands.WorkCommand(Console.Out).Scan(cli.Get("ledger"), cli.Get("root"));
}

// packs signs and verifies bundles offline: no store, no model, no network (Extensibility 0.13 §K6).
if (string.Equals(cli.Command, "packs", StringComparison.OrdinalIgnoreCase))
{
    return new AgentMemory.Cli.Commands.PacksCommand(Console.Out).Execute(cli);
}

// schema-parity is pure static analysis of embedded snapshots — no Neo4j connection or host needed.
if (string.Equals(cli.Command, "schema-parity", StringComparison.OrdinalIgnoreCase))
{
    return new AgentMemory.Cli.Commands.SchemaParityCommand(Console.Out)
        .Execute(cli.Get("upstream-version"), cli.Get("extensions"));
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
                : null,
            cli.Get("batch-resolution-snapshots"),
            cli.Get("coalesced-persistence"),
            cli.Get("pool-size"));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
}

try
{
    // Validate source bytes and the complete composition before creating database services.
    var moduleInput = string.Equals(cli.Command, "modules", StringComparison.OrdinalIgnoreCase)
        ? ModuleCommandInput.Load(cli) : null;

    // No args passed to the builder: configuration comes from environment variables + appsettings, so a
    // bare command token (e.g. "migrate") never trips the configuration command-line provider.
    var builder = Host.CreateApplicationBuilder();
    // Module reports are machine-readable JSON; command failures still go to stderr.
    if (moduleInput is not null) builder.Logging.ClearProviders();
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

    // The operator path for extension DDL. Extensions ship their schema as ext/<id>/000N scripts and
    // MigrationRunner applies exactly the ones Neo4jOptions.Extensions names -- and this block set the
    // URI, credentials, database and dimensions and never touched Extensions, so `migrate` applied base
    // migrations only and no supported path existed for the rest. Resolved through the same
    // CLI > config > env precedence as every other setting.
    //
    // Validated BEFORE the host is built: an unknown id should end in "known: ..." on stderr, not a
    // wrapped exception from inside the DI graph.
    var extensionsArg = Resolve("extensions", string.Empty, "Neo4j:Extensions", "NEO4J_EXTENSIONS");
    // Parsed EAGERLY, here, not inside the lambda below: an options-configure lambda runs lazily on
    // first resolution, so throwing from inside it produces a wrapped exception from the DI graph
    // instead of one readable line. A typo should end in a correction.
    var activeExtensions = CliSchemaExtensions.Parse(extensionsArg);

    builder.Services.AddNeo4jAgentMemory(
        _ => { },
        o =>
        {
            o.Uri = uri;
            o.Username = user;
            o.Password = password;
            o.Database = database;
            o.EmbeddingDimensions = dims;
            if (activeExtensions is not null)
                o.Extensions = new HashSet<string>(activeExtensions, StringComparer.Ordinal);
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
        "decisions" => await new AgentMemory.Cli.Commands.DecisionsCommand(output)
            .ImportAsync(sp.GetRequiredService<INeo4jTransactionRunner>(), cli.Get("root"), cli.Get("source")),
        "work" => await new AgentMemory.Cli.Commands.WorkCommand(output)
            .ImportAsync(sp.GetRequiredService<INeo4jTransactionRunner>(), cli.Get("ledger"), cli.Get("root"), cli.Get("source")),
        "codemap" => await new AgentMemory.Cli.Commands.CodeMapCommand(output)
            .ImportAsync(sp.GetRequiredService<INeo4jTransactionRunner>(), cli.Get("root"), cli.Get("source"), cli.Get("scip"), cli.Get("pack"), cli.Get("trust")),
        "modules" => await new ModulesCommand(
            sp.GetRequiredService<INeo4jTransactionRunner>(), output).ExecuteAsync(moduleInput!),
        "migrate" => await new MigrateCommand(
            sp.GetRequiredService<IMigrationRunner>(), output).ExecuteAsync(),
        "bootstrap" => await new BootstrapCommand(
            sp.GetRequiredService<ISchemaBootstrapper>(), output).ExecuteAsync(),
        "retrim" => await new RetrimCommand(
            sp.GetRequiredService<ISchemaBootstrapper>(), output).ExecuteAsync(cli.HasFlag("apply")),
        // 30.14: the registry is resolved here so the owners report runs. Resolved rather than
        // required, because a host that has not registered extensions still gets the conformance half.
        "schema-check" => await new SchemaCheckCommand(
            sp.GetRequiredService<INeo4jTransactionRunner>(),
            sp.GetRequiredService<IOptions<Neo4jOptions>>(), output,
            sp.GetService<AgentMemory.Neo4j.Schema.Extensions.SchemaExtensionRegistry>()).ExecuteAsync(),
        "consolidate" => await new ConsolidateCommand(
            sp.GetRequiredService<IConsolidationService>(), output).ExecuteAsync(
                cli.HasFlag("apply"), cli.HasFlag("close-generic-entities"), cli.HasFlag("close-unsaid-preferences"), cli.Get("owner"),
                cli.Get("approve")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
        "decay" => await new DecayCommand(
            sp.GetRequiredService<IMemoryDecayService>(), output).ExecuteAsync(cli.Get("owner")),
        "conflicts" => await new ConflictsCommand(
            sp.GetRequiredService<IConflictDetectionService>(), output).ExecuteAsync(),
        "invalidate" => await new InvalidateCommand(
            sp.GetRequiredService<IFactRepository>(),
            sp.GetRequiredService<IEntityRepository>(),
            sp.GetRequiredService<IPreferenceRepository>(), output,
            sp.GetService<IWorkingMemoryService>())
            .ExecuteAsync(cli.Get("type"), cli.Get("id"), cli.Get("owner")),
        "supersede" => await new SupersedeCommand(
            sp.GetRequiredService<IFactRepository>(),
            sp.GetRequiredService<IPreferenceRepository>(), output,
            sp.GetService<IWorkingMemoryService>())
            .ExecuteAsync(cli.Get("type"), cli.Get("loser"), cli.Get("winner"), cli.Get("owner")),
        "history" => await new HistoryCommand(
            sp.GetRequiredService<IMemoryHistoryService>(), output)
            .ExecuteAsync(
                cli.Get("type"), cli.Get("id"), cli.Get("owner"),
                liveOnly: cli.HasFlag("live-only"),
                ownOnly: cli.HasFlag("own-only"),
                limitValue: cli.Get("limit"),
                asOfValue: cli.Get("as-of")),
        // S4. "ours is capable but opaque" -- memory could be queried but not SEEN. Read-only by
        // design: there is deliberately no `block --write`, because a block an agent can hand back
        // becomes the store, and the graph's provenance and supersession records then describe a
        // shadow of what the system believes.
        // G3 (40.47): an owner's data as a whole; erase asks for --confirm.
        "owner" => await new OwnerCommand(
            sp.GetRequiredService<IMemoryOwnerDataService>(), output)
            .ExecuteAsync(cli.Subcommand, cli.Get("owner"), cli.Get("file"), cli.HasFlag("confirm")),
        // G6 (40.50): the store's integrity rules, read-only.
        "integrity" => await new IntegrityCommand(
            sp.GetRequiredService<IMemoryIntegrityService>(), output).ExecuteAsync(cli.Get("owner")),
        "block" => await new BlockCommand(
            sp.GetRequiredService<IMemoryHistoryService>(), output)
            .ExecuteAsync(cli.Get("owner"), cli.Get("limit")),
        // 40.57: validation packs build their own stack per pack (its options, its extraction, a replayed clock),
        // against the same store this host is configured for.
        // CMP-4: the traversal-context trial composes the Coding packs over the same store and compiles each fixture task.
        // Pack format v1.1 §4: trial-gated activation, on this host's own history; the decision is signed with the host key.
        "evaluate" when cli.Get("gate") is not null => await new PackGateCommand(o =>
            {
                o.Uri = uri;
                o.Username = user;
                o.Password = password;
                o.Database = database;
                o.EmbeddingDimensions = dims;
            }, output)
            .ExecuteAsync(cli.Get("gate"), cli.Get("repo"), cli.Get("host-key"), cli.Get("activations"), cli.Get("variant"), cli.Get("output"),
                cli.Get("pack"), cli.Get("trust")),
        "evaluate" when cli.Get("traversal") is not null => await new TraversalTrialCommand(o =>
            {
                o.Uri = uri;
                o.Username = user;
                o.Password = password;
                o.Database = database;
                o.EmbeddingDimensions = dims;
            }, output)
            .ExecuteAsync(cli.Get("traversal"), cli.Get("output"), cli.Get("rounds"), cli.Get("warmup")),
        "evaluate" when cli.HasFlag("pack") => await new PackEvaluationCommand(o =>
            {
                o.Uri = uri;
                o.Username = user;
                o.Password = password;
                o.Database = database;
                o.EmbeddingDimensions = dims;
            }, output)
            .ExecuteAsync(cli.Get("pack"), cli.Get("output")),
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
