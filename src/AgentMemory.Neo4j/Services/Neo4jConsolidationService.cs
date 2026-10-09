using System.Globalization;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="IConsolidationService"/>. Runs the enabled hygiene operations as batch
/// Cypher; dry runs use the <c>Count*</c> projection of each mutating query, so the reported counts
/// match what an apply run does. Applied runs write a <c>:ConsolidationRun</c> audit node. The dreaming
/// operations (AMDREAM001) read what they need, decide in code, and close exactly the ids decided on.
/// </summary>
internal sealed class Neo4jConsolidationService : IConsolidationService
{
    // A "duplicate group" is 2+ nodes sharing the same key; we keep one and report/remove the rest.
    private const int DuplicateGroupMinSize = 2;

    // Source messages checked at once by the preference source check (one chat call each).
    private const int SourceChecksInParallel = 4;

    private readonly INeo4jTransactionRunner _tx;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<Neo4jConsolidationService> _logger;
    private readonly IPreferenceSourceCheck? _sourceCheck;

    public Neo4jConsolidationService(
        INeo4jTransactionRunner tx,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<Neo4jConsolidationService> logger,
        IPreferenceSourceCheck? sourceCheck = null)
    {
        _tx = tx ?? throw new ArgumentNullException(nameof(tx));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sourceCheck = sourceCheck;
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

#pragma warning disable AMDREAM001
        var approved = opts.ApprovedProposals is null ? null : new HashSet<string>(opts.ApprovedProposals, StringComparer.Ordinal);
        var now = ranAt.UtcDateTime.ToString("O");
        var proposals = new List<ConsolidationProposal>();
        int genericEntities = 0, genericConnections = 0, unsaidPreferences = 0;

        if (opts.CloseGenericEntities)
        {
            var (entities, connections) = await DecideGenericEntitiesAsync(opts.OwnerId, approved, cancellationToken).ConfigureAwait(false);
            proposals.AddRange(entities);
            proposals.AddRange(connections);
            genericEntities = await CloseAsync(ConsolidationQueries.CloseEntities, entities, opts.DryRun, now, cancellationToken).ConfigureAwait(false);
            genericConnections = await CloseAsync(ConsolidationQueries.CloseConnections, connections, opts.DryRun, now, cancellationToken).ConfigureAwait(false);
        }

        if (opts.CloseUnsaidPreferences)
        {
            var preferences = await DecideUnsaidPreferencesAsync(opts.OwnerId, approved, cancellationToken).ConfigureAwait(false);
            proposals.AddRange(preferences);
            unsaidPreferences = await CloseAsync(ConsolidationQueries.ClosePreferences, preferences, opts.DryRun, now, cancellationToken).ConfigureAwait(false);
        }

        var report = new ConsolidationReport
        {
            RunId = runId,
            DryRun = opts.DryRun,
            RanAtUtc = ranAt,
            ConversationsArchived = conversationsArchived,
            DuplicatePreferencesRemoved = duplicatePreferencesRemoved,
            DuplicateEntitiesDetected = duplicateEntities,
            LongTraceCandidates = longTraceCandidates,
            GenericEntitiesClosed = genericEntities,
            GenericConnectionsClosed = genericConnections,
            UnsaidPreferencesClosed = unsaidPreferences,
            Proposals = proposals,
        };
#pragma warning restore AMDREAM001

        if (!opts.DryRun)
            await RecordRunAsync(report, ranAt, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Consolidation run {RunId} complete (dryRun={DryRun}): {Archived} archived, {PrefsRemoved} dup-prefs, " +
            "{DupEntities} dup-entity candidates, {LongTraces} long-trace candidates, {GenericEntities} generic entities and " +
            "{GenericConnections} of their connections, {UnsaidPreferences} unsaid preferences.",
            runId, opts.DryRun, conversationsArchived, duplicatePreferencesRemoved, duplicateEntities, longTraceCandidates,
            genericEntities, genericConnections, unsaidPreferences);

        return report;
    }

#pragma warning disable AMDREAM001
    private async Task<(List<ConsolidationProposal> Entities, List<ConsolidationProposal> Connections)> DecideGenericEntitiesAsync(
        string? ownerId, HashSet<string>? approved, CancellationToken cancellationToken)
    {
        var entities = await ReadRowsAsync(ConsolidationQueries.LiveEntities, new { ownerId }, r => new GenericEntityRule.EntityRow(
            r["id"].As<string>(), r["ownerId"].As<string?>(), r["name"].As<string?>() ?? string.Empty, r["type"].As<string?>() ?? string.Empty),
            cancellationToken).ConfigureAwait(false);
        var statements = await ReadRowsAsync(ConsolidationQueries.LiveStatementTexts, new { ownerId },
            r => (OwnerId: r["ownerId"].As<string?>(), Text: r["text"].As<string?>() ?? string.Empty), cancellationToken).ConfigureAwait(false);
        var decision = GenericEntityRule.Decide(entities, statements);
        if (decision.Closed.Count == 0) return ([], []);

        var closedEntities = decision.Closed
            .Select(e => new ConsolidationProposal(e.Id, "entity", e.OwnerId, $"{e.Name} ({e.Type})",
                "a generic entity (an object, or a thing without a name) that a live fact or preference names"))
            .Where(p => approved is null || approved.Contains(p.Id))
            .ToList();
        var connections = await ReadRowsAsync(ConsolidationQueries.LiveConnectionsOf,
            new { closed = decision.Closed.Select(e => e.Id).ToArray(), spared = decision.Spared.Select(e => e.Id).ToArray() },
            r => new ConsolidationProposal(r["id"].As<string>(), "connection", r["ownerId"].As<string?>(),
                $"{r["source"].As<string?>()} -[{r["type"].As<string?>()}]-> {r["target"].As<string?>()}",
                "a connection to a generic entity this run closes"),
            cancellationToken).ConfigureAwait(false);
        return (closedEntities, connections.Where(p => approved is null || approved.Contains(p.Id)).ToList());
    }

    private async Task<List<ConsolidationProposal>> DecideUnsaidPreferencesAsync(
        string? ownerId, HashSet<string>? approved, CancellationToken cancellationToken)
    {
        if (approved is not null)
        {
            // An approved proposal is closed as the dry run reported it; the chat model is not asked again.
            var rows = await ReadRowsAsync(ConsolidationQueries.LivePreferencesById, new { ids = approved.ToArray() },
                r => new ConsolidationProposal(r["id"].As<string>(), "preference", r["ownerId"].As<string?>(),
                    PreferenceText(r["category"].As<string?>(), r["text"].As<string?>()),
                    "approved from a dry run: the message it came from does not say it"),
                cancellationToken).ConfigureAwait(false);
            return rows.Where(p => ownerId is null || p.OwnerId == ownerId).ToList();
        }

        if (_sourceCheck is null)
        {
            _logger.LogWarning(
                "Consolidation: CloseUnsaidPreferences is on but no IPreferenceSourceCheck is registered (the LLM extraction "
                + "package registers one that asks the host's chat model); no preference is closed.");
            return [];
        }

        var sources = await ReadRowsAsync(ConsolidationQueries.LivePreferenceSources, new { ownerId }, r => new PreferenceSourceRow(
            r["id"].As<string>(), r["ownerId"].As<string?>(), r["category"].As<string?>(), r["text"].As<string?>() ?? string.Empty,
            r["messageId"].As<string?>() ?? string.Empty, r["message"].As<string?>() ?? string.Empty, r["saidAt"].As<string?>(),
            r["earlier"].As<List<string>?>() ?? []), cancellationToken).ConfigureAwait(false);

        var closed = new List<ConsolidationProposal>();
        var gate = new object();
        await Parallel.ForEachAsync(
            sources.Where(s => s.Message.Length > 0).GroupBy(s => s.MessageId),
            new ParallelOptions { MaxDegreeOfParallelism = SourceChecksInParallel, CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                var first = group.First();
                var request = new PreferenceSourceRequest(first.Message, ParseInstant(first.SaidAt) ?? _clock.UtcNow, first.Earlier,
                    group.Select(s => new PreferenceSourceItem(s.Id, s.Category, s.Text)).ToList());
                IReadOnlyCollection<string> unsaid;
                try
                {
                    unsaid = await _sourceCheck.FindUnsaidAsync(request, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed check closes nothing: the preference stays as it was.
                    _logger.LogWarning(ex, "Consolidation: the preference source check failed for message {MessageId}; its preferences are kept.", group.Key);
                    return;
                }
                lock (gate)
                {
                    closed.AddRange(group.Where(s => unsaid.Contains(s.Id)).Select(s => new ConsolidationProposal(
                        s.Id, "preference", s.OwnerId, PreferenceText(s.Category, s.Text), "the message it came from does not say it")));
                }
            }).ConfigureAwait(false);
        return closed.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>A live preference with the message it was stored from.</summary>
    internal sealed record PreferenceSourceRow(string Id, string? OwnerId, string? Category, string Text, string MessageId,
        string Message, string? SaidAt, IReadOnlyList<string> Earlier);

    private async Task<int> CloseAsync(string cypher, IReadOnlyCollection<ConsolidationProposal> proposals, bool dryRun, string now,
        CancellationToken cancellationToken)
    {
        if (proposals.Count == 0) return 0;
        if (dryRun) return proposals.Count;
        return await WriteCountAsync(cypher, new { ids = proposals.Select(p => p.Id).ToArray(), now }, cancellationToken).ConfigureAwait(false);
    }
#pragma warning restore AMDREAM001

    /// <summary>As the router arena showed it to the judge: <c>loves spicy food (food)</c>.</summary>
    internal static string PreferenceText(string? category, string? text) =>
        string.IsNullOrWhiteSpace(category) ? text ?? string.Empty : $"{text} ({category})";

    /// <summary>A Cypher <c>toString(datetime)</c>, possibly with a zone id in brackets.</summary>
    private static DateTimeOffset? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var bracket = value.IndexOf('[');
        var text = bracket > 0 ? value[..bracket] : value;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    private Task<List<T>> ReadRowsAsync<T>(string cypher, object parameters, Func<IRecord, T> map, CancellationToken cancellationToken) =>
        _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(map).ToList();
        }, cancellationToken);

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
#pragma warning disable AMDREAM001
            await runner.RunAsync(ConsolidationQueries.RecordConsolidationRun, new
            {
                id = report.RunId,
                ranAt = ranAt.UtcDateTime.ToString("O"),
                conversationsArchived = report.ConversationsArchived,
                preferencesRemoved = report.DuplicatePreferencesRemoved,
                duplicateEntities = report.DuplicateEntitiesDetected,
                longTraceCandidates = report.LongTraceCandidates,
                genericEntitiesClosed = report.GenericEntitiesClosed,
                genericConnectionsClosed = report.GenericConnectionsClosed,
                unsaidPreferencesClosed = report.UnsaidPreferencesClosed,
            }).ConfigureAwait(false);
#pragma warning restore AMDREAM001
        }, cancellationToken);
}
