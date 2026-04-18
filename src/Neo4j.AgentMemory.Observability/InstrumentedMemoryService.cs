using System.Diagnostics;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Observability;

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
            var result = await _inner.RecallAsync(request, cancellationToken);
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
                sessionId, conversationId, role, content, metadata, cancellationToken);
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
            var result = await _inner.AddMessagesAsync(messages, cancellationToken);
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
            var result = await _inner.ExtractAndPersistAsync(request, cancellationToken);
            _metrics.EntitiesExtracted.Add(result.Entities.Count);
            _metrics.FactsExtracted.Add(result.Facts.Count);
            _metrics.PreferencesExtracted.Add(result.Preferences.Count);
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
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.clear_session");
        activity?.SetTag("memory.session_id", sessionId);

        try
        {
            await _inner.ClearSessionAsync(sessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async Task ExtractFromSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extract_from_session");
        activity?.SetTag("memory.session_id", sessionId);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.ExtractFromSessionAsync(sessionId, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.extract_from_conversation");
        activity?.SetTag("memory.conversation_id", conversationId);

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.ExtractFromConversationAsync(conversationId, cancellationToken);
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

    public Task<int> GenerateEmbeddingsBatchAsync(
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        using var activity = MemoryActivitySource.Instance.StartActivity("memory.generate_embeddings_batch");
        activity?.SetTag("memory.node_label", nodeLabel);
        activity?.SetTag("memory.batch_size", batchSize);
        return _inner.GenerateEmbeddingsBatchAsync(nodeLabel, batchSize, cancellationToken);
    }
}
