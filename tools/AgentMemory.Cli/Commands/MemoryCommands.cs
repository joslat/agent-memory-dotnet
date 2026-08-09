using System.Globalization;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Schema.Parity;
using Neo4j.Driver;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Thin, unit-testable command handlers over the shipped maintenance services. Each writes
/// human-readable output to the supplied <see cref="TextWriter"/> and returns a process exit code
/// (0 = success, non-zero = usage/runtime error).
/// </summary>
public sealed class MigrateCommand(IMigrationRunner runner, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        output.WriteLine("Running migrations...");
        await runner.RunMigrationsAsync(cancellationToken);
        output.WriteLine("Migrations complete.");
        return 0;
    }
}

/// <summary>Bootstraps the schema (constraints, indexes, vector indexes).</summary>
public sealed class BootstrapCommand(ISchemaBootstrapper bootstrapper, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        output.WriteLine("Bootstrapping schema (constraints + indexes)...");
        await bootstrapper.BootstrapAsync(cancellationToken);
        output.WriteLine("Schema bootstrap complete.");
        return 0;
    }
}

/// <summary>
/// Verifies that the LIVE database has every constraint and index the bootstrap creates (runtime
/// conformance). Exit 0 when conformant, 1 (listing the missing objects) otherwise. This is the runtime
/// counterpart to <c>bootstrap</c> — distinct from <c>schema-parity</c>, which is a static check that the
/// .NET schema is compatible with the embedded upstream snapshot.
/// </summary>
public sealed class SchemaCheckCommand(
    INeo4jTransactionRunner txRunner,
    IOptions<Neo4jOptions> options,
    TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var database = options.Value.Database;
        var expected = SchemaConformance.ExpectedObjectNames(options.Value.EmbeddingDimensions);

        var existing = await txRunner.ReadAsync(async runner =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var query in new[] { SchemaQueries.ShowConstraintNames, SchemaQueries.ShowIndexNames })
            {
                var cursor = await runner.RunAsync(query);
                var records = await cursor.ToListAsync();
                foreach (var record in records)
                {
                    var name = record["name"].As<string>();
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
            }
            return names;
        }, cancellationToken) ?? new HashSet<string>(StringComparer.Ordinal);

        // A store written by 1.3.0 or earlier has no canonical fact keys, because the *_key
        // properties did not exist. Facts are now MERGEd on {subject_key, predicate_key, object_key,
        // owner_key}, so until BootstrapAsync has backfilled them, an upsert of an existing triple
        // matches nothing and silently creates a DUPLICATE. BootstrapAsync is the documented startup
        // step and does run the backfill - but a host that skips it gets no signal at all, and
        // schema-check is exactly where an operator looks for that signal.
        var legacyFacts = await txRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(FactQueries.SelectFactsMissingCanonicalKeys, new { limit = 1 });
            var records = await cursor.ToListAsync();
            return records.Count;
        }, cancellationToken);

        var missing = SchemaConformance.MissingObjects(expected, existing);
        if (missing.Count == 0 && legacyFacts == 0)
        {
            output.WriteLine(
                $"schema-check: OK — all {expected.Count} expected constraints/indexes are present in database '{database}'.");
            return 0;
        }

        if (legacyFacts > 0)
        {
            output.WriteLine(
                $"schema-check: facts in database '{database}' are missing canonical keys (pre-1.4 data). " +
                "Run ISchemaBootstrapper.BootstrapAsync() before writing, or upserts will create duplicates " +
                "instead of matching the existing facts.");
            if (missing.Count == 0) return 1;
        }

        output.WriteLine(
            $"schema-check: FAILED — {missing.Count} of {expected.Count} expected schema objects are missing from database '{database}':");
        foreach (var name in missing)
            output.WriteLine($"  - {name}");
        output.WriteLine("Run 'agentmemory bootstrap' (or 'migrate') to create them.");
        return 1;
    }
}

/// <summary>Runs the consolidation / hygiene pass (dry-run unless <c>apply</c> is set).</summary>
public sealed class ConsolidateCommand(IConsolidationService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(bool apply, CancellationToken cancellationToken = default)
    {
        var report = await service.ConsolidateAsync(new ConsolidationOptions { DryRun = !apply }, cancellationToken);

        output.WriteLine($"Consolidation {(report.DryRun ? "DRY-RUN (no changes written)" : "APPLIED")} — run {report.RunId}");
        output.WriteLine($"  Conversations archived:        {report.ConversationsArchived}");
        output.WriteLine($"  Duplicate preferences removed: {report.DuplicatePreferencesRemoved}");
        output.WriteLine($"  Duplicate entities detected:   {report.DuplicateEntitiesDetected}");
        output.WriteLine($"  Long-trace candidates:         {report.LongTraceCandidates}");
        if (report.DryRun)
            output.WriteLine("  Re-run with --apply to perform the mutating operations.");
        return 0;
    }
}

/// <summary>Detects fact contradictions (detect-only) and prints the report.</summary>
public sealed class ConflictsCommand(IConflictDetectionService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var report = await service.DetectConflictsAsync(cancellationToken: cancellationToken);

        output.WriteLine($"Conflict detection — {report.FactConflictCount} fact contradiction group(s).");
        foreach (var conflict in report.FactConflicts)
        {
            var owner = conflict.OwnerId is null ? "shared" : $"owner={conflict.OwnerId}";
            output.WriteLine($"  [{owner}] {conflict.Subject} / {conflict.Predicate}:");
            foreach (var value in conflict.Values)
                // InvariantCulture so the confidence decimal point is stable across locales (R6 cleanup).
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"      = {value.Object}  (fact {value.FactId}, conf {value.Confidence:0.00})"));
        }
        if (report.FactConflictCount == 0)
            output.WriteLine("  No contradictions found.");
        return 0;
    }
}

/// <summary>
/// Decay-prunes memories: by default (<see cref="MemoryDecayOptions.NonDestructive"/>) it soft-invalidates
/// low-score nodes (kept, recoverable, dropped from live recall); set <c>MemoryDecay:NonDestructive=false</c>
/// to hard-delete. With <c>--owner &lt;id&gt;</c> the prune is owner-scoped (the owner's own nodes only —
/// never another owner's, never shared/global). Without it the prune is global (admin).
/// </summary>
public sealed class DecayCommand(IMemoryDecayService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? ownerId, CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrWhiteSpace(ownerId) ? null : MemoryScope.For(ownerId);
        var pruned = await service.PruneExpiredMemoriesAsync(scope, cancellationToken);

        var target = scope is null ? "all owners (global)" : $"owner '{ownerId}'";
        output.WriteLine($"Pruned {pruned} expired memory node(s) for {target}.");
        return 0;
    }
}

/// <summary>
/// Self-verifies the .NET schema against an embedded upstream snapshot (the parity compatibility kit).
/// Pure static analysis — needs no Neo4j connection — so it is safe to run in CI as a regression gate.
/// Exit 0 = compatible, 1 = a compatibility break (or an unknown/absent version).
/// </summary>
public sealed class SchemaParityCommand(TextWriter output)
{
    public int Execute(string? upstreamVersion)
    {
        var registry = new UpstreamSchemaRegistry();
        var available = registry.AvailableVersions;
        if (available.Count == 0)
        {
            output.WriteLine("error: no upstream schema snapshots are embedded.");
            return 1;
        }

        var target = string.IsNullOrWhiteSpace(upstreamVersion) ? available[^1] : upstreamVersion;
        if (!available.Contains(target))
        {
            output.WriteLine($"error: unknown upstream version '{target}'. Available: {string.Join(", ", available)}.");
            return 1;
        }

        var report = SchemaParityVerifier.VerifyDotNet(target, registry);
        output.WriteLine(report.Summary());
        return report.IsCompatible ? 0 : 1;
    }
}

/// <summary>
/// Soft-invalidates a long-term node by id (D5): it leaves live recall but is kept and stays visible to
/// as-of recall before invalidation. Owner-scoped when <c>--owner</c> is given. Exit 0 if a node was
/// invalidated, 1 if nothing matched in scope or on a usage error.
/// <para>Resolves the repositories directly (not <see cref="ILongTermMemoryService"/>) so this ops command
/// needs no embedding backend — invalidation is a pure id-based write.</para>
/// </summary>
public sealed class InvalidateCommand(
    IFactRepository facts, IEntityRepository entities, IPreferenceRepository preferences, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? type, string? id, string? owner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
        {
            output.WriteLine("error: invalidate requires --type <fact|entity|preference> and --id <id>.");
            return 1;
        }

        var scope = string.IsNullOrWhiteSpace(owner) ? null : MemoryScope.For(owner);
        var kind = type.Trim().ToLowerInvariant();
        Task<bool>? op = kind switch
        {
            "fact" => facts.InvalidateAsync(id, scope, cancellationToken),
            "entity" => entities.InvalidateAsync(id, scope, cancellationToken),
            "preference" or "pref" => preferences.InvalidateAsync(id, scope, cancellationToken),
            _ => null,
        };
        if (op is null)
        {
            output.WriteLine($"error: unknown --type '{type}' (expected fact|entity|preference).");
            return 1;
        }

        var ownerNote = scope is null ? string.Empty : $" (owner '{owner}')";
        if (await op)
        {
            output.WriteLine($"Invalidated {kind} '{id}'{ownerNote}.");
            return 0;
        }
        output.WriteLine($"No matching {kind} '{id}'{ownerNote} to invalidate.");
        return 1;
    }
}

/// <summary>
/// Supersedes a loser long-term node with a winner (D7): closes the loser non-destructively and links
/// <c>:SUPERSEDED_BY</c>. Owner-scoped when <c>--owner</c> is given (both nodes must belong to the owner).
/// Exit 0 if superseded, 1 if nothing matched in scope or on a usage error. Resolves repositories directly
/// (no embedding backend needed).
/// </summary>
public sealed class SupersedeCommand(IFactRepository facts, IPreferenceRepository preferences, TextWriter output)
{
    public async Task<int> ExecuteAsync(string? type, string? loser, string? winner, string? owner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(loser) || string.IsNullOrWhiteSpace(winner))
        {
            output.WriteLine("error: supersede requires --type <fact|preference>, --loser <id>, and --winner <id>.");
            return 1;
        }

        var scope = string.IsNullOrWhiteSpace(owner) ? null : MemoryScope.For(owner);
        var kind = type.Trim().ToLowerInvariant();
        Task<bool>? op = kind switch
        {
            "fact" => facts.SupersedeAsync(loser, winner, scope, cancellationToken),
            "preference" or "pref" => preferences.SupersedeAsync(loser, winner, scope, cancellationToken),
            _ => null,
        };
        if (op is null)
        {
            output.WriteLine($"error: unknown --type '{type}' (expected fact|preference).");
            return 1;
        }

        var ownerNote = scope is null ? string.Empty : $" (owner '{owner}')";
        if (await op)
        {
            output.WriteLine($"Superseded {kind} '{loser}' with '{winner}'{ownerNote}.");
            return 0;
        }
        output.WriteLine($"No matching {kind} loser+winner in scope{ownerNote}; nothing superseded.");
        return 1;
    }
}
/// <summary>
/// Lists long-term memory lifecycle records across entities, facts, and preferences. This is a read-only
/// audit/history surface: by default it includes live and soft-invalidated rows; <c>--live-only</c> narrows
/// to live recall candidates.
/// </summary>
public sealed class HistoryCommand(IMemoryHistoryService service, TextWriter output)
{
    public async Task<int> ExecuteAsync(
        string? type,
        string? id,
        string? owner,
        bool liveOnly,
        bool ownOnly,
        string? limitValue,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseKind(type, out var kind))
        {
            output.WriteLine($"error: unknown --type '{type}' (expected fact|entity|preference). omit --type to list all.");
            return 1;
        }

        if (!TryParseLimit(limitValue, out var limit))
        {
            output.WriteLine("error: history --limit must be a positive integer.");
            return 1;
        }

        var query = new MemoryHistoryQuery
        {
            Kind = kind,
            Id = NullIfWhiteSpace(id),
            OwnerId = NullIfWhiteSpace(owner),
            IncludeInvalidated = !liveOnly,
            IncludeShared = !ownOnly,
            Limit = limit,
        };

        var records = await service.GetHistoryAsync(query, cancellationToken).ConfigureAwait(false);
        output.WriteLine($"history: {records.Count} memory record(s){DescribeQuery(query)}.");

        foreach (var record in records)
        {
            var ownerText = string.IsNullOrWhiteSpace(record.OwnerId) ? "shared" : $"owner={record.OwnerId}";
            var status = record.Status.ToString().ToLowerInvariant();
            output.WriteLine($"  [{record.Kind.ToString().ToLowerInvariant()}] {record.Id} {status} {ownerText} created={Format(record.CreatedAtUtc)}");
            if (!string.IsNullOrWhiteSpace(record.Summary))
                output.WriteLine($"      {record.Summary}");
            if (record.UpdatedAtUtc is not null)
                output.WriteLine($"      updated_at: {Format(record.UpdatedAtUtc.Value)}");
            if (record.InvalidatedAtUtc is not null)
                output.WriteLine($"      invalidated_at: {Format(record.InvalidatedAtUtc.Value)}");
            if (record.LastAccessedAtUtc is not null || record.AccessCount > 0 || record.ReadAuditCount > 0)
                output.WriteLine($"      access: count={record.AccessCount}, last={FormatOptional(record.LastAccessedAtUtc)}, audit_rows={record.ReadAuditCount}, last_audit={FormatOptional(record.LastReadAuditAtUtc)}");
            if (record.ValidFromUtc is not null || record.ValidUntilUtc is not null)
                output.WriteLine($"      valid_time: {FormatOptional(record.ValidFromUtc)} -> {FormatOptional(record.ValidUntilUtc)}");
            if (record.SupersededByIds.Count > 0)
                output.WriteLine($"      superseded_by: {string.Join(", ", record.SupersededByIds)}");
            if (record.SupersedesIds.Count > 0)
                output.WriteLine($"      supersedes: {string.Join(", ", record.SupersedesIds)}");
            if (record.SourceMessageIds.Count > 0)
                output.WriteLine($"      source_messages: {string.Join(", ", record.SourceMessageIds)}");
        }

        return 0;
    }

    private static bool TryParseKind(string? type, out MemoryHistoryKind? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(type)) return true;

        kind = type.Trim().ToLowerInvariant() switch
        {
            "entity" or "entities" => MemoryHistoryKind.Entity,
            "fact" or "facts" => MemoryHistoryKind.Fact,
            "preference" or "preferences" or "pref" or "prefs" => MemoryHistoryKind.Preference,
            _ => null,
        };
        return kind is not null;
    }

    private static bool TryParseLimit(string? value, out int limit)
    {
        limit = 50;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit > 0;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DescribeQuery(MemoryHistoryQuery query)
    {
        var parts = new List<string>();
        if (query.Kind is not null) parts.Add($"type={query.Kind.ToString()!.ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(query.Id)) parts.Add($"id={query.Id}");
        if (!string.IsNullOrWhiteSpace(query.OwnerId)) parts.Add(query.IncludeShared ? $"owner={query.OwnerId}+shared" : $"owner={query.OwnerId}");
        if (!query.IncludeInvalidated) parts.Add("live-only");
        parts.Add($"limit={query.Limit}");
        return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatOptional(DateTimeOffset? value) => value is null ? "open" : Format(value.Value);
}
