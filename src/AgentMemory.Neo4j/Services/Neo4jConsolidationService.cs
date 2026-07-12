using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="IConsolidationService"/>. Runs the enabled hygiene operations as batch
/// Cypher; dry runs use the <c>Count*</c> projection of each mutating query, so the reported counts
/// match what an apply run does. Applied runs write a <c>:ConsolidationRun</c> audit node.
/// </summary>
internal sealed class Neo4jConsolidationService : IConsolidationService
{
    // A "duplicate group" is 2+ nodes sharing the same key; we keep one and report/remove the rest.
    private const int DuplicateGroupMinSize = 2;

    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<Neo4jConsolidationService> _logger;

    public Neo4jConsolidationService(
        INeo4jTransactionRunner tx,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<Neo4jConsolidationService> logger)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<ConsolidationReport> ConsolidateAsync(
        ConsolidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new ConsolidationOptions();
        var ranAt = _clock.UtcNow;
        var runId = _idGenerator.GenerateId();
        var cutoff = (ranAt - opts.ConversationExpiry).UtcDateTime.ToString("O");

        _logger.LogInformation("Consolidation run {RunId} starting (dryRun={DryRun}).", runId, opts.DryRun);

        var conversationsArchived = 0;
        var duplicatePreferencesRemoved = 0;
        var duplicateEntities = 0;
        var longTraceCandidates = 0;

        if (opts.ArchiveExpiredConversations)
        {
            conversationsArchived = opts.DryRun
                ? await ReadCountAsync(ConsolidationQueries.CountExpiredConversations, new { cutoff }, cancellationToken).ConfigureAwait(false)
                : await WriteCountAsync(ConsolidationQueries.ArchiveExpiredConversations, new { cutoff }, cancellationToken).ConfigureAwait(false);
        }

        if (opts.RemoveDuplicatePreferences)
        {
            duplicatePreferencesRemoved = opts.DryRun
                ? await ReadCountAsync(ConsolidationQueries.CountDuplicatePreferences, new { minGroupSize = DuplicateGroupMinSize }, cancellationToken).ConfigureAwait(false)
                : await WriteCountAsync(ConsolidationQueries.RemoveDuplicatePreferences, new { minGroupSize = DuplicateGroupMinSize }, cancellationToken).ConfigureAwait(false);
        }

        // Detection-only operations (no apply path): entity merge needs careful edge redirection and
        // trace summarization needs an LLM — both are reported here as candidates and left as follow-ups.
        if (opts.DetectDuplicateEntities)
            duplicateEntities = await ReadCountAsync(ConsolidationQueries.CountDuplicateEntities, new { minGroupSize = DuplicateGroupMinSize }, cancellationToken).ConfigureAwait(false);

        if (opts.DetectLongTraces)
            longTraceCandidates = await ReadCountAsync(
                ConsolidationQueries.CountLongTraces, new { threshold = opts.LongTraceStepThreshold }, cancellationToken).ConfigureAwait(false);

        var report = new ConsolidationReport
        {
            RunId = runId,
            DryRun = opts.DryRun,
            RanAtUtc = ranAt,
            ConversationsArchived = conversationsArchived,
            DuplicatePreferencesRemoved = duplicatePreferencesRemoved,
            DuplicateEntitiesDetected = duplicateEntities,
            LongTraceCandidates = longTraceCandidates,
        };

        if (!opts.DryRun)
            await RecordRunAsync(report, ranAt, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Consolidation run {RunId} complete (dryRun={DryRun}): {Archived} archived, {PrefsRemoved} dup-prefs, " +
            "{DupEntities} dup-entity candidates, {LongTraces} long-trace candidates.",
            runId, opts.DryRun, conversationsArchived, duplicatePreferencesRemoved, duplicateEntities, longTraceCandidates);

        return report;
    }

    private Task<int> ReadCountAsync(string cypher, object parameters, CancellationToken cancellationToken) =>
        _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["count"].As<int>();
        }, cancellationToken);

    private Task<int> WriteCountAsync(string cypher, object parameters, CancellationToken cancellationToken) =>
        _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["count"].As<int>();
        }, cancellationToken);

    private Task RecordRunAsync(ConsolidationReport report, DateTimeOffset ranAt, CancellationToken cancellationToken) =>
        _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(ConsolidationQueries.RecordConsolidationRun, new
            {
                id = report.RunId,
                ranAt = ranAt.UtcDateTime.ToString("O"),
                conversationsArchived = report.ConversationsArchived,
                preferencesRemoved = report.DuplicatePreferencesRemoved,
                duplicateEntities = report.DuplicateEntitiesDetected,
                longTraceCandidates = report.LongTraceCandidates,
            }).ConfigureAwait(false);
        }, cancellationToken);
}
