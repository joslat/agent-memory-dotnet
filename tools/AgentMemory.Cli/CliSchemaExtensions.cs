using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;

namespace AgentMemory.Cli;

/// <summary>
/// Turns <c>--extensions &lt;id,…&gt;</c> into activated schema extensions for the CLI host.
/// </summary>
/// <remarks>
/// <para>
/// <b>The operator path for extension DDL.</b> Extensions ship their schema as
/// <c>ext/&lt;id&gt;/000N</c> scripts, and <c>MigrationRunner</c> applies exactly the ones
/// <see cref="Neo4jOptions.Extensions"/> names. Until this existed the CLI host set the URI,
/// credentials, database and embedding dimensions and never touched <c>Extensions</c> — so
/// <c>agentmemory migrate</c>, the one command an operator runs to bring a database up to date,
/// applied base migrations only.
/// </para>
/// <para>
/// That gap failed <i>quietly</i>, which is what made it worth closing rather than documenting. A host
/// can enable <c>arithmetic</c> in code and meet a live graph with no <c>fact_derivation_key_idx</c>:
/// the queries still run, the MERGE still converges, and the only symptom is a scan where a seek
/// belonged. Nothing errors, so nothing is investigated.
/// </para>
/// <para>
/// Applied to <b>every</b> host-backed verb, not just <c>migrate</c>. <c>schema-check</c>'s owners
/// report reads the same set to decide which shapes should be present, so a flag honoured on one verb
/// and ignored on the next would make the two disagree about the same database.
/// </para>
/// </remarks>
internal static class CliSchemaExtensions
{
    /// <summary>The extension ids this build ships, for error messages and <c>--help</c>.</summary>
    public static IReadOnlyList<string> KnownIds { get; } =
        SchemaExtensionRegistry.CreateDefault().KnownIds;

    /// <summary>
    /// Applies the parsed <c>--extensions</c> value to <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A null or blank argument is <b>no override</b>, not an instruction to clear: configuration
    /// binding and <c>appsettings</c> can populate <c>Extensions</c> before this runs, and treating an
    /// absent flag as "deactivate everything" would make the CLI silently narrower than the library it
    /// drives.
    /// </para>
    /// <para>
    /// An explicit list <b>replaces</b> rather than merges. The flag is the operator's statement of what
    /// this run activates; merging would make <c>--extensions arithmetic</c> mean "arithmetic and
    /// whatever else was configured", which is not what anyone typing it intends and cannot be un-said
    /// from the command line.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">An id no shipped extension declares.</exception>
    public static void Apply(Neo4jOptions options, string? argument)
    {
        ArgumentNullException.ThrowIfNull(options);

        var requested = Parse(argument);
        if (requested is null) return;

        options.Extensions = new HashSet<string>(requested, StringComparer.Ordinal);
    }

    /// <summary>
    /// Validates the argument and returns the ids, or <see langword="null"/> when there is no override.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Apply"/> so the CLI can validate <b>before</b> building the host. The
    /// options-configure lambda runs lazily, on first resolution, so throwing from inside it surfaces
    /// as a wrapped exception from the DI graph — the very thing validating early is supposed to
    /// prevent. Calling this eagerly turns a typo into one line on stderr.
    /// </remarks>
    /// <exception cref="ArgumentException">An id no shipped extension declares.</exception>
    public static IReadOnlyList<string>? Parse(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument)) return null;

        var requested = argument
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (requested.Count == 0) return null;

        // Validated HERE rather than left to the registry inside host construction. The registry does
        // reject an unknown id -- that refusal is the whole point of the activation mechanism -- but it
        // does so from inside the DI graph, where the operator sees a wrapped exception instead of
        // "unknown extension 'aritmetic'; known: ...". A typo should end in a correction, not a stack
        // trace.
        //
        // Case-sensitive on purpose: the id is part of the (:Migration).version key ("ext/arithmetic/
        // 0001"). Accepting "Arithmetic" and storing it verbatim would orphan every previously-applied
        // row for that extension, splitting one history in two with no error at any point.
        var unknown = requested
            .Where(id => !KnownIds.Contains(id, StringComparer.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"unknown schema extension(s): {string.Join(", ", unknown)}. "
                + $"Known: {string.Join(", ", KnownIds)}.",
                nameof(argument));
        }

        return requested;
    }
}
