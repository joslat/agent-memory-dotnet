namespace AgentMemory.Cli;

/// <summary>
/// Parsed command-line invocation: the first bare token is the command; <c>--key value</c>,
/// <c>--key=value</c>, and bare <c>--flag</c> become options. Parsed independently of the host's
/// configuration so a bare command token (e.g. <c>migrate</c>) never trips the config command-line provider.
/// </summary>
public sealed class CliArgs
{
    public string? Command { get; }
    public IReadOnlyDictionary<string, string?> Options { get; }

    private CliArgs(string? command, IReadOnlyDictionary<string, string?> options)
    {
        Command = command;
        Options = options;
    }

    /// <summary>True if the flag was present (with or without a value).</summary>
    public bool HasFlag(string name) => Options.ContainsKey(name);

    /// <summary>The option's value, or null if absent / value-less.</summary>
    public string? Get(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public static CliArgs Parse(string[] args)
    {
        string? command = null;
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var body = token[2..];
                var eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    options[body[..eq]] = body[(eq + 1)..];
                }
                // The next token is this option's VALUE unless it is itself a long option ("--..."). Only
                // "--" is treated as a separate option, NOT any "-", so a dash-leading value like
                // `--owner -42` or `--password -s3cret` is consumed correctly. (Treating any "-" as a new
                // option silently dropped such values — e.g. `--owner -42` became a null owner, widening a
                // scoped destructive prune to ALL owners.)
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options[body] = args[++i];
                }
                else
                {
                    options[body] = null; // bare flag
                }
            }
            else
            {
                command ??= token;
            }
        }

        return new CliArgs(command, options);
    }
}

/// <summary>Usage text for the CLI.</summary>
public static class CliHelp
{
    public static void Print(TextWriter output)
    {
        output.WriteLine("""
            agentmemory — operational CLI for AgentMemory for .NET

            USAGE:
              agentmemory <command> [options]

            COMMANDS:
              migrate                Apply pending Cypher migrations.
              bootstrap              Create schema constraints and indexes.
              schema-check           Verify the LIVE database has every constraint/index the bootstrap
                                     creates (runtime conformance). Exit 1 listing any missing objects.
              consolidate [--apply]  Run the memory-hygiene pass (dry-run unless --apply).
              conflicts              Detect fact contradictions (detect-only).
              invalidate --type <fact|entity|preference> --id <id> [--owner <id>]
                                     Soft-invalidate a node (D5): drops from live recall, kept + as-of-recallable.
              supersede --type <fact|preference> --loser <id> --winner <id> [--owner <id>]
                                     Supersede a loser with a winner (D7): non-destructive, links :SUPERSEDED_BY.
              history [--type <fact|entity|preference>] [--id <id>] [--owner <id>]
                      [--live-only] [--own-only] [--limit <n>]
                                     Read long-term memory lifecycle history, including soft-invalidated
                                     rows, supersession links, valid-time windows, and source messages.
              evaluate [--iterations <n>] [--owner <id>] [--output <path>]
                                     Run deterministic memory-layer quality/performance scenarios and
                                     write a JSON report under artifacts/evaluation by default.
              perf [--label <name>] [--scenarios <ids|all>] [--iterations <n>] [--warmup <n>]
                   [--latency <zero|remote>] [--embedding-dimensions <n>] [--output <dir>]
                                     Measure a complete agent TURN: database round trips, embedding
                                     requests, model calls, and per-stage timing. Provisions its own
                                     Neo4j via Testcontainers (Docker required) with deterministic
                                     embeddings and a scripted model, so counters are reproducible.
                                     Writes a dated run directory under performance/runs by default.
              decay [--owner <id>]   Decay-prune memories: soft-invalidate by default (kept + recoverable;
                                     set MemoryDecay:NonDestructive=false to hard-delete). Owner-scoped, or global.
              schema-parity [--upstream-version <v>]
                                     Verify the .NET schema is compatible with an embedded upstream
                                     neo4j-agent-memory snapshot (default: newest). No DB needed; exit 1
                                     on a break. CI-friendly self-check.
              help                   Show this help.

            CONNECTION (precedence: CLI option > Neo4j:* config > NEO4J_* env > default):
              --uri <bolt-uri>       Default bolt://localhost:7687  (or Neo4j:Uri / NEO4J_URI)
              --user <name>          Default neo4j                  (or Neo4j:Username / NEO4J_USERNAME)
              --password <secret>    Default neo4j                  (or Neo4j:Password / NEO4J_PASSWORD)
              --database <name>      Default neo4j                  (or Neo4j:Database / NEO4J_DATABASE)
              --embedding-dimensions <n>  Default 1536              (or Neo4j:EmbeddingDimensions / NEO4J_EMBEDDING_DIMENSIONS)

            EXAMPLES:
              agentmemory migrate --uri bolt://db:7687 --password s3cret
              agentmemory consolidate            # dry-run report
              agentmemory consolidate --apply    # perform hygiene mutations
              agentmemory decay                  # global prune (all owners)
              agentmemory decay --owner user-42  # prune only user-42's memories
              agentmemory history --type fact --owner user-42 --limit 20
              agentmemory evaluate --iterations 3 --output artifacts/evaluation/local.json
              agentmemory perf --label baseline --iterations 10
              agentmemory perf --label feat-01-access-tracking --latency remote
            """);
    }
}
