using AgentMemory.Core.Stubs;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Neo4j.Repositories.Neo4jRecordMapper;

namespace AgentMemory.Neo4j.Repositories;

internal sealed class Neo4jMessageRepository : IMessageRepository
{
    // Unscoped searches filter after the global vector candidate pool (metadata filters, forgotten messages), so
    // they over-fetch and then take `limit`. Session-scoped searches use an exact in-session query instead.
    private const int ScopedOverFetchFactor = 5;
    private const int ScopedOverFetchFloor = 50;
    /// <summary>
    /// G1 (40.45): every time this repository stamps or compares comes from here, so a host (or a replay) that sets the
    /// clock sets all of it. Without DI, the system clock.
    /// </summary>
    private readonly TimeProvider _clock;
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jMessageRepository> _logger;
    private readonly bool _useOptimizedMessageBatchWrites;
    /// <summary>RVW-ERASE-CONVERSATIONS: the ambient owner a message is saved for (the Agent Framework providers and the
    /// MCP store tool set it). Absent or empty, a save stamps no owner, as before.</summary>
    private readonly IMemoryOwnerContext? _ownerContext;

    public Neo4jMessageRepository(
        INeo4jTransactionRunner tx,
        ILogger<Neo4jMessageRepository> logger,
        IOptions<Neo4jOptions>? options = null,
        TimeProvider? clock = null,
        IMemoryOwnerContext? ownerContext = null)
    {
        _tx = tx;
        _logger = logger;
        _useOptimizedMessageBatchWrites = options?.Value.UseOptimizedMessageBatchWrites ?? true;
        _clock = clock ?? TimeProvider.System;
        _ownerContext = ownerContext;
    }

    /// <summary>
    /// RVW-ERASE-CONVERSATIONS: with an owner known, every conversation the write touches is created for that owner or
    /// adopted when it has none, and one another owner holds is refused before any message is written (the same
    /// transaction, so a refusal leaves nothing). With no owner the write is unchanged.
    /// </summary>
    private async Task ClaimConversationsAsync(IAsyncQueryRunner runner, IReadOnlyList<Message> messages)
    {
        var owner = _ownerContext?.UserId;
        if (string.IsNullOrEmpty(owner)) return;
        var conversations = messages
            .GroupBy(m => m.ConversationId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(m => m.TimestampUtc).First())
            .Select(m => new Dictionary<string, object?>
            {
                ["id"] = m.ConversationId,
                ["session_id"] = m.SessionId,
                ["timestamp"] = m.TimestampUtc.ToString("O"),
            })
            .ToList();
        var cursor = await runner.RunAsync(MessageQueries.ClaimConversations, new { conversations, ownerId = owner }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        var held = rows.FirstOrDefault(row => !string.Equals(row["owner"] as string, owner, StringComparison.Ordinal));
        if (held is not null)
            throw new UnauthorizedAccessException(
                $"Conversation '{held["id"].As<string>()}' belongs to another owner; no message was added to it.");
    }

    public async Task<Message> AddAsync(Message message, CancellationToken cancellationToken = default)
    {
        RefuseReserved(message);
        _logger.LogDebug("Adding message {Id} to conversation {ConvId}", message.MessageId, message.ConversationId);

        return await _tx.WriteAsync(async runner =>
        {
            await ClaimConversationsAsync(runner, [message]).ConfigureAwait(false);
            // Store embedding separately to handle null
            var createParams = new Dictionary<string, object?>
            {
                ["id"]             = message.MessageId,
                ["conversationId"] = message.ConversationId,
                ["sessionId"]      = message.SessionId,
                ["role"]           = message.Role,
                ["content"]        = message.Content,
                ["timestamp"]      = message.TimestampUtc.ToString("O"),
                ["toolCallIds"]    = message.ToolCallIds?.ToList() ?? new List<string>(),
                ["metadata"]       = SerializeMetadata(message.Metadata),
                ["embedding"]      = message.Embedding is { Length: > 0 }
                    ? message.Embedding.ToList()
                    : null
            };

            var cursor = await runner.RunAsync(MessageQueries.Add, createParams).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var returned = record["m"];
            var properties = returned is INode node
                ? node.Properties
                : returned.As<IReadOnlyDictionary<string, object>>();

            return MapToMessage(properties, message.Embedding);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// REV4-6: an owner import writes its memories' sources as <c>imported-message-</c> markers, derived from the owner
    /// and the original id. A message written at one would become those memories' source, quoted to that owner by recall,
    /// so no message may take such an id.
    /// </summary>
    private static void RefuseReserved(Message message)
    {
        if (message.MessageId?.StartsWith(Services.OwnerIdentityPortability.ImportedMessagePrefix, StringComparison.Ordinal) == true)
            throw new ArgumentException($"Message ids starting with '{Services.OwnerIdentityPortability.ImportedMessagePrefix}' are reserved for owner import.",
                nameof(message));
    }

    public async Task<IReadOnlyList<Message>> AddBatchAsync(IEnumerable<Message> messages, CancellationToken cancellationToken = default)
    {
        var ordered = messages.OrderBy(m => m.TimestampUtc).ToList();
        if (ordered.Count == 0) return Array.Empty<Message>();
        foreach (var message in ordered) RefuseReserved(message);

        _logger.LogDebug("Batch adding {Count} messages", ordered.Count);

        var msgParams = ordered.Select(m => new Dictionary<string, object?>
        {
            ["id"]              = m.MessageId,
            ["conversation_id"] = m.ConversationId,
            ["session_id"]      = m.SessionId,
            ["role"]            = m.Role,
            ["content"]         = m.Content,
            ["timestamp"]       = m.TimestampUtc.ToString("O"),
            ["tool_call_ids"]   = m.ToolCallIds?.ToList() ?? new List<string>(),
            ["metadata"]        = SerializeMetadata(m.Metadata),
            ["embedding"]       = m.Embedding is { Length: > 0 }
                ? m.Embedding.ToList()
                : null
        }).ToList();

        var embeddingMap = ordered.ToDictionary(m => m.MessageId, m => m.Embedding);
        // RVW-REST-HOSTS (hosts-3): a message whose id another conversation holds is written under the qualified id.
        foreach (var m in ordered)
            embeddingMap.TryAdd(Services.MessageIds.ConversationQualified(m.MessageId, m.ConversationId), m.Embedding);
        if (_useOptimizedMessageBatchWrites)
        {
            return await _tx.WriteAsync(async runner =>
            {
                await ClaimConversationsAsync(runner, ordered).ConfigureAwait(false);
                var cursor = await runner.RunAsync(
                    MessageQueries.AddBatchOptimized,
                    new { messages = msgParams }).ConfigureAwait(false);
                var records = await cursor.ToListAsync().ConfigureAwait(false);
                // DRT-10: the read-back is the mapped fields only; the caller already holds the embeddings.
                return records.Select(record =>
                {
                    var returned = record["m"];
                    var properties = returned is INode node ? node.Properties : returned.As<IReadOnlyDictionary<string, object>>();
                    var id = properties["id"].As<string>();
                    return MapToMessage(
                        properties,
                        embeddingMap.TryGetValue(id, out var embedding) ? embedding : null);
                }).ToList();
            }, cancellationToken).ConfigureAwait(false);
        }

        return await _tx.WriteAsync(async runner =>
        {
            await ClaimConversationsAsync(runner, ordered).ConfigureAwait(false);
            var cursor = await runner.RunAsync(MessageQueries.AddBatch, new { messages = msgParams }).ConfigureAwait(false);
            // RVW-REST-HOSTS (hosts-3): the id each message was written under (qualified when another conversation held
            // the one it came with); every statement below works on those.
            var used = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in await cursor.ToListAsync().ConfigureAwait(false))
                used[row["requested"].As<string>()] = row["used"].As<string>();
            string Used(Message m) => used.TryGetValue(m.MessageId, out var id) ? id : m.MessageId;

            // Set embeddings — only for messages with a real (non-empty) vector.
            foreach (var msg in ordered.Where(m => m.Embedding is { Length: > 0 }))
            {
                await runner.RunAsync(
                    SharedFragments.SetMessageEmbedding,
                    new { id = Used(msg), embedding = msg.Embedding!.ToList() }).ConfigureAwait(false);
            }

            // Create NEXT_MESSAGE chain within batch
            for (int i = 1; i < ordered.Count; i++)
            {
                await runner.RunAsync(
                    MessageQueries.CreateNextMessageLink,
                    new { prevId = Used(ordered[i - 1]), nextId = Used(ordered[i]) }).ConfigureAwait(false);
            }

            // Connect first batch message to any existing last message in the conversation
            if (ordered.Count > 0)
            {
                await runner.RunAsync(
                    MessageQueries.LinkBatchToExisting,
                    new
                    {
                        conversationId = ordered[0].ConversationId,
                        batchIds       = ordered.Select(Used).ToList(),
                        firstId        = Used(ordered[0])
                    }).ConfigureAwait(false);
            }

            // Re-read all created messages
            var readCursor = await runner.RunAsync(
                MessageQueries.GetByIds,
                new { ids = ordered.Select(Used).ToList() }).ConfigureAwait(false);
            var records = await readCursor.ToListAsync().ConfigureAwait(false);

            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                var id = node["id"].As<string>();
                return MapToMessage(node, embeddingMap.TryGetValue(id, out var emb) ? emb : null);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Message?> GetByIdAsync(string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting message {Id}", messageId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.GetById, new { id = messageId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            if (records.Count == 0) return null;
            var node = records[0]["m"].As<INode>();
            return MapToMessage(node, ReadEmbedding(node));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Overrides the interface default (which loops <c>GetByIdAsync</c>) with one query, reusing the
    /// existing <c>GetByIds</c> statement the batch-write path already re-reads through — so this adds
    /// no Cypher to the catalogue, only a second caller.
    /// <para>
    /// Embeddings are read back per node rather than skipped: the caller is projection, which does not
    /// need them, but a repository method that silently returned half-populated messages would be a
    /// trap for the next caller that does.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> GetByIdsAsync(
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return Array.Empty<Message>();

        _logger.LogDebug("Getting {Count} message(s) by id", messageIds.Count);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                MessageQueries.GetByIds, new { ids = messageIds.ToList() }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<Message>)records.Select(record =>
            {
                var node = record["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetByConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting messages for conversation {Id}", conversationId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.GetByConversation, new { conversationId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetRecentBySessionAsync(string sessionId, int limit, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting {Limit} recent messages for session {SessionId}", limit, sessionId);

        return await _tx.ReadSingleAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.GetRecentBySession, new { sessionId, limit }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetExtractionDeferredAsync(
        IReadOnlyCollection<string> messageIds, string? heldFor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0) return;
        await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.SetExtractionDeferred,
                new { messageIds = messageIds.ToList(), heldFor }).ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetExtractionDeferredAsync(
        string heldFor, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heldFor);
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.GetExtractionDeferred,
                new { heldFor, limit = Math.Max(1, limit) }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => MapToMessage(r["m"].As<INode>(), embedding: null)).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetAllBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting ALL messages for session {SessionId} (uncapped, chronological)", sessionId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.GetAllBySession, new { sessionId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Message Message, double Score)>> SearchByVectorAsync(
        float[] queryEmbedding,
        string? sessionId = null,
        int limit = 10,
        double minScore = 0.0,
        IReadOnlyDictionary<string, object>? metadataFilters = null,
        CancellationToken cancellationToken = default)
    {
        // Boundary invariant: a zero-dimension (empty/degraded) query embedding has no semantic signal and
        // would throw a dimension mismatch at db.index.vector.queryNodes — short-circuit to an empty result.
        if (queryEmbedding is not { Length: > 0 }) return Array.Empty<(Message, double)>();
        _logger.LogDebug("Vector search messages, sessionId={SessionId}, limit={Limit}", sessionId, limit);

        var (filterClause, filterParams) = MetadataFilterBuilder.Build(metadataFilters, nodeAlias: "node");
        var hasMetadataFilter = !string.IsNullOrWhiteSpace(filterClause);
        // Unscoped, the index answers first and filters apply after: metadata filters, and forgotten messages
        // (G-30), which rank first for a question about what was forgotten. Over-fetch so the result is not short
        // (unless more than topK - limit forgotten messages outrank every live one).
        var topK = sessionId is null
            ? Math.Max(limit * ScopedOverFetchFactor, limit + ScopedOverFetchFloor)
            : limit;
        var cypher = MessageQueries.SearchByVector(sessionId is not null, filterClause, topK);

        var parameters = new Dictionary<string, object>
        {
            ["embedding"] = queryEmbedding.ToList(),
            ["minScore"]  = minScore,
            ["limit"]     = limit
        };
        if (sessionId is not null) parameters["sessionId"] = sessionId;
        foreach (var (k, v) in filterParams) parameters[k] = v;

        return await _tx.ReadSingleAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher, parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node  = r["node"].As<INode>();
                var score = r["score"].As<double>();
                return (MapToMessage(node, ReadEmbedding(node)), score);
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting messages for session {SessionId}", sessionId);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(MessageQueries.DeleteBySession, new { sessionId }).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string messageId, bool cascade = true, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting message {Id}, cascade={Cascade}", messageId, cascade);

        return await _tx.WriteAsync(async runner =>
        {
            var query = cascade ? MessageQueries.DeleteCascade : MessageQueries.DeleteSimple;
            var cursor = await runner.RunAsync(query, new { id = messageId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> InvalidateAsync(string messageId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Invalidating message {Id}", messageId);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(MessageQueries.Invalidate,
                new { id = messageId, now = _clock.GetUtcNow().ToString("O") }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["invalidated"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Message>> GetRecentBySessionAsOfAsync(
        string sessionId,
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Getting recent messages for session {SessionId} as of {AsOf}, limit={Limit}", sessionId, asOf, limit);

        return await _tx.ReadSingleAsync(async runner =>
        {
            var cursor = await runner.RunAsync(TemporalQueries.GetRecentMessagesAsOf, new
            {
                sessionId,
                asOf = asOf.UtcDateTime.ToString("O"),
                limit
            }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r =>
            {
                var node = r["m"].As<INode>();
                return MapToMessage(node, ReadEmbedding(node));
            }).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Message MapToMessage(INode node, float[]? embedding) =>
        MapToMessage(node.Properties, embedding);

    private static Message MapToMessage(IReadOnlyDictionary<string, object> properties, float[]? embedding) =>
        new()
        {
            MessageId      = properties["id"].As<string>(),
            ConversationId = properties["conversation_id"].As<string>(),
            SessionId      = properties["session_id"].As<string>(),
            Role           = properties["role"].As<string>(),
            Content        = properties["content"].As<string>(),
            TimestampUtc   = Neo4jDateTimeHelper.ReadDateTimeOffset(properties["timestamp"]),
            InvalidatedAtUtc = properties.TryGetValue("invalidated_at", out var forgotten) && forgotten is not null
                                ? Neo4jDateTimeHelper.ReadDateTimeOffset(forgotten)
                                : null,
            Embedding      = embedding,
            ToolCallIds    = properties.TryGetValue("tool_call_ids", out var tc)
                                ? tc.As<IList<object>>().Select(v => v.ToString()!).ToList()
                                : [],
            Metadata       = DeserializeMetadata(properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };

    private static float[]? ReadEmbedding(INode node)
    {
        if (!node.Properties.TryGetValue("embedding", out var ev) || ev is null) return null;
        return ev.As<IList<object>>().Select(v => Convert.ToSingle(v)).ToArray();
    }
}
