using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Observability;

/// <summary>
/// Decorator that wraps <see cref="IMemoryService"/> with OpenTelemetry tracing and metrics.
/// </summary>
internal sealed class InstrumentedMemoryService : IMemoryService
{
    private readonly IMemoryService _inner;
    private readonly MemoryMetrics _metrics;

    public InstrumentedMemoryService(IMemoryService inner, MemoryMetrics metrics)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.recall");
        activity?.SetTag("memory.session_id", request.SessionId);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.RecallAsync(request, cancellationToken).ConfigureAwait(false);
            _metrics.RecallRequests.Add(1);
            activity?.SetTag("memory.recall.entity_count", result.Context.RelevantEntities.Items.Count);
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

    public Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
        // Single-clock recall == bitemporal recall with both clocks equal (D6).
        => RecallAsOfAsync(request, asOf, asOf, cancellationToken);

    public async Task<RecallResult> RecallAsOfAsync(
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
            return result;
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
        if (userId is not null) activity?.SetTag("memory.user_id", userId);

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
        if (userId is not null) activity?.SetTag("memory.user_id", userId);

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
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        // Must be async/await: a synchronous method would dispose the activity the moment it returns the
        // still-pending Task, so the span would close with ~0 duration, disconnected from the actual work.
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.generate_embeddings_batch");
        activity?.SetTag("memory.node_label", nodeLabel);
        activity?.SetTag("memory.batch_size", batchSize);
        return await _inner.GenerateEmbeddingsBatchAsync(nodeLabel, batchSize, cancellationToken).ConfigureAwait(false);
    }
}
