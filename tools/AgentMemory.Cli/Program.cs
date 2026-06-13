using AgentMemory;                          // meta AddNeo4jAgentMemory
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Cli;
using AgentMemory.Cli.Commands;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var cli = CliArgs.Parse(args);

if (cli.Command is null || string.Equals(cli.Command, "help", StringComparison.OrdinalIgnoreCase))
{
    CliHelp.Print(Console.Out);
    return cli.Command is null ? 1 : 0;
}

var known = new[] { "migrate", "bootstrap", "consolidate", "decay", "conflicts", "schema-parity", "invalidate", "supersede" };
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

    using var host = builder.Build();
    using var scope = host.Services.CreateScope();
    var sp = scope.ServiceProvider;
    var output = Console.Out;

    return cli.Command.ToLowerInvariant() switch
    {
        "migrate" => await new MigrateCommand(
            sp.GetRequiredService<IMigrationRunner>(), output).ExecuteAsync(),
        "bootstrap" => await new BootstrapCommand(
            sp.GetRequiredService<ISchemaBootstrapper>(), output).ExecuteAsync(),
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
        _ => 1,
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
