using System.Diagnostics;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Observability;

/// <summary>
/// Decorator that wraps <see cref="IMemoryService"/> with OpenTelemetry tracing and metrics.
/// </summary>
internal sealed class InstrumentedMemoryService : IMemoryService
{
    private readonly IMemoryService _inner;
    private readonly MemoryMetrics _metrics;
    private readonly IngestionFailureMode _failureMode;
    private readonly bool _includeOwnerId;

    public InstrumentedMemoryService(
        IMemoryService inner,
        MemoryMetrics metrics,
        IOptions<ExtractionOptions>? extractionOptions = null,
        IOptions<ObservabilityOptions>? observabilityOptions = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _failureMode = extractionOptions?.Value.FailureMode ?? IngestionFailureMode.BestEffort;
        _includeOwnerId = observabilityOptions?.Value.IncludeOwnerIdInTelemetry ?? false;
    }

    /// <summary>
    /// Emits which recall sections came back empty or short.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The misses are the roadmap, and nothing recorded them.</b> A <c>:MemoryReadAudit</c> row is
    /// created inside <c>MATCH (n:{label} {id: $id})</c>, so a row exists only for a <b>hit</b> — there
    /// was no record anywhere that an owner asked for something and memory had nothing.
    /// </para>
    /// <para>
    /// A counter rather than a stored node, deliberately: a node per miss grows without bound on the
    /// exact workload that produces the most misses, and would need its own retention policy to stop
    /// being a liability. A counter answers the operational question — how often, for which category —
    /// without storing anything.
    /// </para>
    /// <para>
    /// <b>Requires <c>RecallOptions.IncludeDiagnostics</c>.</b> Without it the sections carry no
    /// diagnostics and nothing is emitted, rather than emitting a guess: "empty" and "never searched"
    /// are different, and only the diagnostics can tell them apart.
    /// </para>
    /// </remarks>
    private void RecordSectionOutcomes(MemoryContext context)
    {
        Record("recent", context.RecentMessages.Diagnostics);
        Record("relevant_messages", context.RelevantMessages.Diagnostics);
        Record("entities", context.RelevantEntities.Diagnostics);
        Record("preferences", context.RelevantPreferences.Diagnostics);
        Record("facts", context.RelevantFacts.Diagnostics);
        Record("traces", context.SimilarTraces.Diagnostics);

        void Record(string category, MemoryContextSectionDiagnostics? diagnostics)
        {
            if (diagnostics is null) return;

            var tag = new KeyValuePair<string, object?>("memory.category", category);
            if (diagnostics.SearchedAndEmpty) _metrics.RecallSectionEmpty.Add(1, tag);
            else if (diagnostics.SearchedAndShort) _metrics.RecallSectionShort.Add(1, tag);
        }
    }

    /// <summary>
    /// Tags the owner dimension without exporting tenant data unless the host asked for it.
    /// </summary>
    /// <remarks>
    /// A tenant identifier in a trace is tenant data in a system usually less access-controlled than
    /// the database it came from. The boolean answers the operational question -- was this operation
    /// scoped? -- without carrying the value, which is the same choice every owner-scoped vector
    /// search here already makes with <c>memory.vector.owner_scoped</c>.
    /// </remarks>
    private void TagOwner(System.Diagnostics.Activity? activity, string? userId)
    {
        if (activity is null) return;

        if (_includeOwnerId && userId is not null)
            activity.SetTag("memory.user_id", userId);
        else
            activity.SetTag("memory.owner_scoped", userId is not null);
    }

    public async Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.recall");
        activity?.SetTag("memory.session_id", request.SessionId);
        TagOwner(activity, request.UserId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.RecallAsync(request, cancellationToken).ConfigureAwait(false);
            _metrics.RecallRequests.Add(1);
            activity?.SetTag("memory.recall.entity_count", result.Context.RelevantEntities.Items.Count);
            activity?.SetTag("memory.recall.total_items", result.TotalItemsRetrieved);
            RecordSectionOutcomes(result.Context);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.RecallDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
        // Single-clock recall == bitemporal recall with both clocks equal (D6): default systemAsOf to asOf.
        => RecallAsOfCoreAsync(request, asOf, systemAsOf ?? asOf, cancellationToken);

    private async Task<RecallResult> RecallAsOfCoreAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.recall_as_of");
        activity?.SetTag("memory.session_id", request.SessionId);
        activity?.SetTag("memory.valid_as_of", validAsOf.ToString("O"));
        activity?.SetTag("memory.system_as_of", systemAsOf.ToString("O"));

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.RecallAsOfAsync(request, validAsOf, systemAsOf, cancellationToken).ConfigureAwait(false);
            _metrics.RecallRequests.Add(1);
            activity?.SetTag("memory.recall.total_items", result.TotalItemsRetrieved);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.RecallDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.add_message");
        activity?.SetTag("memory.session_id", sessionId);
        activity?.SetTag("memory.conversation_id", conversationId);
        activity?.SetTag("memory.message.role", role);

        try
        {
            var result = await _inner.AddMessageAsync(
                sessionId, conversationId, role, content, metadata, cancellationToken).ConfigureAwait(false);
            _metrics.MessagesStored.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    // Explicit override is mandatory, not optional polish: AddMessageWithIdAsync is a default interface
    // method on IMemoryIngestion. Without this override, calls made through an IMemoryService-typed
    // reference (which is how every consumer resolves this decorator) would silently execute the DIM's
    // own default body -- which drops messageId and forwards to plain AddMessageAsync -- defeating the
    // entire idempotent-persistence mechanism for any observability-enabled host, with no error or trace.
    public async Task<Message> AddMessageWithIdAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        string messageId,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.add_message");
        activity?.SetTag("memory.session_id", sessionId);
        activity?.SetTag("memory.conversation_id", conversationId);
        activity?.SetTag("memory.message.role", role);
        activity?.SetTag("memory.message.id", messageId);

        try
        {
            var result = await _inner.AddMessageWithIdAsync(
                sessionId, conversationId, role, content, messageId, metadata, cancellationToken).ConfigureAwait(false);
            _metrics.MessagesStored.Add(1);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.add_messages");

        try
        {
            var result = await _inner.AddMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
            _metrics.MessagesStored.Add(result.Count);
            activity?.SetTag("memory.messages.count", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extract_and_persist");
        activity?.SetTag("memory.session_id", request.SessionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExtractAndPersistAsync(request, cancellationToken).ConfigureAwait(false);
            // NOTE: entity/fact/preference counts are owned by the per-extractor decorators
            // (InstrumentedEntityExtractor etc.), which are the single source of truth for these
            // counters. Counting them here as well would double-count. We keep only span tags.
            activity?.SetTag("memory.extraction.entity_count", result.Entities.Count);
            activity?.SetTag("memory.extraction.fact_count", result.Facts.Count);
            activity?.SetTag("memory.extraction.preference_count", result.Preferences.Count);
            RecordIngestionOutcome(result, activity);
            return result;
        }
        catch (MemoryIngestionException ex)
        {
            // Fail-fast (#101): still surfaces the completed-outcomes item counters, then re-throws.
            RecordItemOutcomes(ex.CompletedOutcomes);
            _metrics.IngestionOperations.Add(1,
                new KeyValuePair<string, object?>("status", nameof(IngestionStatus.Failed)),
                new KeyValuePair<string, object?>("failure_mode", _failureMode.ToString()));
            _metrics.ExtractionErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _metrics.ExtractionErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.ExtractionDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Emits ingestion metrics/tags for a completed (non-throwing) ingestion (#101).</summary>
    private void RecordIngestionOutcome(ExtractionResult result, Activity? activity)
    {
        activity?.SetTag("memory.ingestion.status", result.Status.ToString());
        _metrics.IngestionOperations.Add(1,
            new KeyValuePair<string, object?>("status", result.Status.ToString()),
            new KeyValuePair<string, object?>("failure_mode", _failureMode.ToString()));
        RecordItemOutcomes(result.Outcomes);
    }

    /// <summary>Emits per-item succeeded/failed counters, tagged by kind and stage only (#101) —
    /// never owner IDs, memory text, or exception messages.</summary>
    private void RecordItemOutcomes(IReadOnlyList<IngestionItemOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            var tags = new[]
            {
                new KeyValuePair<string, object?>("item.kind", outcome.Kind.ToString()),
                new KeyValuePair<string, object?>("stage", outcome.Stage.ToString()),
            };
            if (outcome.Status == IngestionItemStatus.Succeeded)
                _metrics.IngestionItemsSucceeded.Add(1, tags);
            else if (outcome.Status == IngestionItemStatus.Failed)
                _metrics.IngestionItemsFailed.Add(1, tags);
        }
    }

    public async Task ClearSessionAsync(
        string sessionId,
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.clear_session");
        activity?.SetTag("memory.session_id", sessionId);

        try
        {
            await _inner.ClearSessionAsync(sessionId, ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task ExtractFromSessionAsync(
        string sessionId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extract_from_session");
        activity?.SetTag("memory.session_id", sessionId);
        TagOwner(activity, userId);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.ExtractFromSessionAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _metrics.ExtractionErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.ExtractionDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extract_from_conversation");
        activity?.SetTag("memory.conversation_id", conversationId);
        TagOwner(activity, userId);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.ExtractFromConversationAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _metrics.ExtractionErrors.Add(1);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            _metrics.ExtractionDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<int> GenerateEmbeddingsBatchAsync(
        MemoryNodeKind nodeKind,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        // Must be async/await: a synchronous method would dispose the activity the moment it returns the
        // still-pending Task, so the span would close with ~0 duration, disconnected from the actual work.
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.generate_embeddings_batch");
        activity?.SetTag("memory.node_kind", nodeKind.ToString());
        activity?.SetTag("memory.batch_size", batchSize);
        return await _inner.GenerateEmbeddingsBatchAsync(nodeKind, batchSize, cancellationToken).ConfigureAwait(false);
    }
}
