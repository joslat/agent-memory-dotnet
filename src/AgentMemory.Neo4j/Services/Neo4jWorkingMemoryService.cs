using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Compiles the per-owner working-memory block and stores it on upstream's <c>:User</c> node.
/// </summary>
/// <remarks>
/// <para>
/// Registered unconditionally and self-gated on <see cref="WorkingMemoryOptions.Enabled"/>, the
/// reranker pattern: gating the registration would mean a host that enables the tier through
/// <c>IOptions</c> reconfiguration still gets nothing, silently.
/// </para>
/// <para>
/// <b>Full rebuild, never partial invalidation.</b> Working out which writes touch which block inputs
/// is the clever answer that goes stale, and a stale block asserting a superseded value manufactures
/// failures in the weakest measured question type. One compile is three reads and at most one write.
/// </para>
/// </remarks>
internal sealed class Neo4jWorkingMemoryService : IWorkingMemoryService
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly WorkingMemoryOptions _options;
    private readonly ILogger<Neo4jWorkingMemoryService> _logger;
    private readonly WorkingMemoryRebuildBackoff? _backoff;

    public Neo4jWorkingMemoryService(
        INeo4jTransactionRunner tx,
        IClock clock,
        IIdGenerator ids,
        IOptions<MemoryOptions> options,
        ILogger<Neo4jWorkingMemoryService> logger,
        WorkingMemoryRebuildBackoff? backoff = null)
    {
        _backoff = backoff;
        ArgumentNullException.ThrowIfNull(options);
        _tx = tx;
        _clock = clock;
        _ids = ids;
        _options = options.Value.WorkingMemory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RebuildAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        // The null-owner skip is guard G3 and it is TCK-load-bearing. See ShouldSkip.
        if (ShouldSkip(ownerId)) return;

        var now = _clock.UtcNow;
        var text = await ComposeAsync(ownerId, now, cancellationToken).ConfigureAwait(false);
        var validUntil = await NextValidityBoundaryAsync(ownerId, now, cancellationToken).ConfigureAwait(false);
        // 37.5. A block with a "Lately" line is a view of a sliding window: it is rebuilt at least daily, writes or not.
        if (text.Contains("Lately (", StringComparison.Ordinal))
            validUntil = EarlierOf(validUntil, now.AddDays(1));
        // The boundary is part of what is stored, so it is part of the hash: a rebuild whose text is
        // unchanged but whose next boundary moved still writes.
        var hash = Hash(validUntil is null ? text : $"{text}\n@{validUntil}");

        // Hash short-circuit: a rebuild that changes nothing writes nothing, so built_at moves only
        // when the CONTENT moves. Without this, every write burst would churn a transaction and
        // invalidate prompt-prefix caching for a block that did not change.
        var stored = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                WorkingMemoryQueries.GetBlockHash, new { ownerId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : records[0]["hash"].As<string?>();
        }, cancellationToken).ConfigureAwait(false);

        if (string.Equals(stored, hash, StringComparison.Ordinal))
        {
            _logger.LogDebug("Working-memory block for owner {Owner} unchanged; skipping write.", ownerId);
            return;
        }

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(WorkingMemoryQueries.UpsertBlock, new
            {
                ownerId,
                id = _ids.GenerateId(),
                block = text,
                hash,
                validUntil,
                now = now.ToString("O", CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Rebuilt working-memory block for owner {Owner} ({Chars} chars).", ownerId, text.Length);
    }

    /// <inheritdoc/>
    public async Task<WorkingMemoryBlock?> GetAsync(
        string ownerId, CancellationToken cancellationToken = default)
    {
        if (ShouldSkip(ownerId)) return null;

        var block = await ReadBlockAsync(ownerId, cancellationToken).ConfigureAwait(false);
        // A fact in the block expired (or a future one became valid) since it was built, or the block was
        // cleared: rebuild once and serve the rebuilt block. Rare by construction: at most once per boundary.
        if (block.ValidUntil is { } boundary && boundary <= _clock.UtcNow)
        {
            // A rebuild that just failed for this owner is not retried on every recall: that would be a
            // failing write and a warning per turn, forever, for as long as the cause lasts.
            if (_backoff?.IsWaiting(ownerId, _clock.UtcNow) == true)
                return _options.ClearOnRebuildFailure ? null : block.Block;
            try
            {
                await RebuildAsync(ownerId, cancellationToken).ConfigureAwait(false);
                block = await ReadBlockAsync(ownerId, cancellationToken).ConfigureAwait(false);
                _backoff?.Succeeded(ownerId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // This is the recall path: a rebuild that cannot write (a read-only connection, a timeout,
                // a lock) must not fail recall. What is served instead follows ClearOnRebuildFailure, the
                // same choice the write path makes: nothing rather than a block that may assert an expired
                // fact, unless the host prefers the stale block.
                _logger.LogWarning(exception,
                    "Working-memory rebuild on read failed for owner {Owner}; recall continues without a rebuilt block.",
                    ownerId);
                _backoff?.Failed(ownerId, _clock.UtcNow);
                return _options.ClearOnRebuildFailure ? null : block.Block;
            }
        }
        return block.Block;
    }

    private async Task<string?> NextValidityBoundaryAsync(string ownerId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            // Only facts that can be in the block: a boundary of one that cannot would rebuild for nothing.
            var cursor = await runner.RunAsync(WorkingMemoryQueries.NextValidityBoundary, new
            {
                ownerId,
                minMentions = _options.MinFactMentionCount,
                now = now.ToString("O", CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 || records[0]["boundary"] is null
                ? null
                : Neo4jDateTimeHelper.ReadNullableDateTimeOffset(records[0]["boundary"])?.ToString("O", CultureInfo.InvariantCulture);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<(WorkingMemoryBlock? Block, DateTimeOffset? ValidUntil)> ReadBlockAsync(
        string ownerId, CancellationToken cancellationToken) =>
        await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                WorkingMemoryQueries.GetBlock, new { ownerId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return ((WorkingMemoryBlock?)null, (DateTimeOffset?)null);

            var validUntil = records[0].Keys.Contains("validUntil")
                ? Neo4jDateTimeHelper.ReadNullableDateTimeOffset(records[0]["validUntil"])
                : null;
            // No text is no block, but the boundary still counts: a block that is empty today because its
            // only fact starts on Monday must be rebuilt on Monday.
            var text = records[0]["block"].As<string?>();
            if (string.IsNullOrEmpty(text)) return (null, validUntil);

            return (new WorkingMemoryBlock
            {
                OwnerId = ownerId,
                Text = text,
                BuiltAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(records[0]["builtAt"])
                             ?? _clock.UtcNow,
                ContentHash = records[0]["hash"].As<string?>() ?? Hash(text),
            }, validUntil);
        }, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task ClearAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        if (ShouldSkip(ownerId)) return;

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(WorkingMemoryQueries.ClearBlock, new
            {
                ownerId,
                now = _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether this call does nothing: the tier is off, or there is no concrete owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>GUARD G3 — the null-owner skip is TCK-load-bearing.</b> The TCK bridge's <c>/add_fact</c> and
    /// <c>/add_preference</c> route through <c>LongTermMemoryService</c>, so the rebuild epilogue fires
    /// during a conformance run whenever this extension is on. Bridge writes are <b>ownerless</b>. Without
    /// this skip, <c>MERGE (:User {identifier: null})</c> runs, and a null unique key turns Bronze
    /// <i>and</i> Gold cases into 500s — the extension would break upstream parity for everyone who
    /// enabled it.
    /// </para>
    /// <para>
    /// There is also no shared-bucket <c>:User</c> to build: an ownerless write belongs to no identity,
    /// and inventing one would be guessing. Skipping is the same choice made elsewhere for the same
    /// reason.
    /// </para>
    /// <para>
    /// A red-first test removes this skip and watches the ownerless path fail, so no future
    /// simplification can delete it innocently.
    /// </para>
    /// </remarks>
    private bool ShouldSkip(string ownerId) =>
        !_options.Enabled || string.IsNullOrWhiteSpace(ownerId);

    /// <summary>Compiles the block text. Deterministic: same inputs, same bytes.</summary>
    internal async Task<string> ComposeAsync(
        string ownerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stamp = now.ToString("O", CultureInfo.InvariantCulture);

        var facts = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(WorkingMemoryQueries.SelectStableFacts, new
            {
                ownerId,
                now = stamp,
                minMentions = _options.MinFactMentionCount,
                byMentions = Math.Max(0, _options.MaxStableFacts - _options.RecentStableFactSlots),
                recent = Math.Min(_options.RecentStableFactSlots, _options.MaxStableFacts),
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(r => $"{r["subject"].As<string>()} {r["predicate"].As<string>()} {r["object"].As<string>()}"
                    // 36.1. The recall renderers' rule, so the block and the recalled facts agree on a date.
                    + (_options.IncludeDates
                        ? AgentMemory.Core.Services.FactDates.Suffix(
                            Neo4jDateTimeHelper.ReadNullableDateTimeOffset(r["validFrom"]),
                            DatePrecisionProperty.FromStored(r["validFromPrecision"] as string),
                            Neo4jDateTimeHelper.ReadNullableDateTimeOffset(r["validUntil"]),
                            DatePrecisionProperty.FromStored(r["validUntilPrecision"] as string),
                            Neo4jDateTimeHelper.ReadNullableDateTimeOffset(r["occurredOn"]),
                            DatePrecisionProperty.FromStored(r["occurredOnPrecision"] as string))
                        : string.Empty))
                .ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        var preferences = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(WorkingMemoryQueries.SelectActivePreferences, new
            {
                ownerId,
                minConfidence = _options.MinPreferenceConfidence,
                limit = _options.MaxActivePreferences,
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(r => $"[{r["category"].As<string>()}] {r["preference"].As<string>()}")
                .ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        var entities = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(WorkingMemoryQueries.SelectTopEntities, new
            {
                ownerId,
                limit = _options.MaxTopEntities,
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(r => $"{r["name"].As<string>()} ({r["type"].As<string>()})")
                .ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        // 37.5. Only when asked: no query, and the block as it was, otherwise.
        var lately = _options.RecentTopicsDays <= 0 ? [] : await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(WorkingMemoryQueries.SelectRecentTopics, new
            {
                ownerId,
                since = now.AddDays(-_options.RecentTopicsDays).ToString("O"),
                now = now.ToString("O"),
                minMentions = Math.Max(1, _options.MinRecentTopicMentions),
                limit = Math.Max(1, _options.MaxRecentTopics),
                selfWords = AgentMemory.Core.Extraction.PersistenceStage.UserNames.SelfWords,
                selfKeys = AgentMemory.Core.Extraction.PersistenceStage.UserNames.SelfWords
                    .Select(AgentMemory.Core.Memory.MemoryTripleCanonicalizer.CanonicalValue).Distinct().ToList(),
                namingKeys = AgentMemory.Core.Extraction.PersistenceStage.UserNames.NamingPredicates
                    .Select(AgentMemory.Core.Memory.MemoryTripleCanonicalizer.Canonical).Distinct().ToList(),
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(r => $"{r["topic"].As<string>()} ({r["mentions"].As<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)} mentions)")
                .ToList();
        }, cancellationToken).ConfigureAwait(false) ?? [];

        return Compose(facts, preferences, entities, _options.MaxTokens, lately, _options.RecentTopicsDays);
    }

    /// <summary>
    /// Renders the three sections within the token budget.
    /// </summary>
    /// <remarks>
    /// Trimming drops whole trailing lines, <b>entities first, then preferences, then facts</b>. Facts
    /// are the head of the question distribution — name, job, stable attributes — so they are the last
    /// thing sacrificed to the budget. Pure and internal so the ordering and the budget can be tested
    /// without a database.
    /// </remarks>
    internal static string Compose(
        IReadOnlyList<string> facts,
        IReadOnlyList<string> preferences,
        IReadOnlyList<string> entities,
        int maxTokens,
        IReadOnlyList<string>? lately = null,
        int latelyDays = 0)
    {
        var factLines = facts.ToList();
        var preferenceLines = preferences.ToList();
        var entityLines = entities.ToList();
        var latelyTopics = (lately ?? []).ToList();

        while (true)
        {
            var rendered = Render(factLines, preferenceLines, entityLines, latelyTopics, latelyDays);
            if (EstimateTokens(rendered) <= maxTokens || rendered.Length == 0) return rendered;

            // "Lately" first: it is the newest and least essential line, and a partial list still reads right.
            if (latelyTopics.Count > 0) latelyTopics.RemoveAt(latelyTopics.Count - 1);
            else if (entityLines.Count > 0) entityLines.RemoveAt(entityLines.Count - 1);
            else if (preferenceLines.Count > 0) preferenceLines.RemoveAt(preferenceLines.Count - 1);
            else if (factLines.Count > 0) factLines.RemoveAt(factLines.Count - 1);
            else return string.Empty;
        }
    }

    private static string Render(
        IReadOnlyList<string> facts, IReadOnlyList<string> preferences, IReadOnlyList<string> entities,
        IReadOnlyList<string> lately, int latelyDays)
    {
        var builder = new StringBuilder();

        void Section(string title, IReadOnlyList<string> lines)
        {
            if (lines.Count == 0) return;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(title).Append('\n');
            builder.Append(string.Join("\n", lines));
        }

        Section("Stable facts:", facts);
        Section("Active preferences:", preferences);
        Section("Key entities:", entities);
        if (lately.Count > 0)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append("Lately (").Append(latelyDays.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" days): ").Append(string.Join(", ", lately));
        }

        return builder.ToString();
    }

    /// <summary>The earlier of a stored boundary (ISO-8601, or null) and <paramref name="instant"/>, as ISO-8601.</summary>
    internal static string EarlierOf(string? boundary, DateTimeOffset instant) =>
        boundary is not null &&
        DateTimeOffset.TryParse(boundary, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at <= instant
            ? boundary
            : instant.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The estimator the budget is expressed in: ceil(chars / 4).</summary>
    internal static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <remarks>
    /// <c>ToHexString(...).ToLowerInvariant()</c> rather than <c>ToHexStringLower</c>: this package
    /// multi-targets down to net8.0, where the latter does not exist.
    /// </remarks>
    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
