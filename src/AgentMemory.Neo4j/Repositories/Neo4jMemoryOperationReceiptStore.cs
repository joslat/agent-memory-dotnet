using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>
/// Applied-once receipts as properties of the message an operation extracted from (<c>applied_operation_id</c>,
/// <c>applied_at_utc</c>, and the ids it wrote and closed). No new label or constraint: the message id is already unique,
/// and a <c>ConversationTurn</c> has exactly one user message. Writes go through the shared transaction runner, so inside
/// a persistence transaction the receipt commits with the effects or not at all. Owner erase deletes the owner's
/// conversations and messages, and their receipts with them.
/// </summary>
internal sealed class Neo4jMemoryOperationReceiptStore(INeo4jTransactionRunner tx) : IMemoryOperationReceiptStore
{
    private const string GetQuery = """
        MATCH (m:Message {id: $messageId})
        WHERE m.applied_operation_id IS NOT NULL
        RETURN m.applied_operation_id AS operationId, m.applied_at_utc AS appliedAt, m.applied_fact_ids AS facts,
               m.applied_preference_ids AS preferences, m.applied_closed_ids AS closed
        """;

    private const string RecordQuery = """
        MATCH (m:Message {id: $messageId})
        WITH m, m.applied_operation_id AS before
        FOREACH (_ IN CASE WHEN before IS NULL THEN [1] ELSE [] END |
          SET m.applied_operation_id = $operationId, m.applied_at_utc = $appliedAt, m.applied_fact_ids = $facts,
              m.applied_preference_ids = $preferences, m.applied_closed_ids = $closed)
        RETURN before
        """;

    public Task<MemoryOperationReceipt?> GetAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        return tx.ReadAsync<MemoryOperationReceipt?>(async runner =>
        {
            var cursor = await runner.RunAsync(GetQuery, new Dictionary<string, object> { ["messageId"] = messageId })
                .ConfigureAwait(false);
            if (!await cursor.FetchAsync().ConfigureAwait(false)) return null;
            var record = cursor.Current;
            return new MemoryOperationReceipt
            {
                OperationId = (string)record["operationId"],
                MessageId = messageId,
                AppliedAtUtc = DateTimeOffset.Parse((string)record["appliedAt"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                FactIds = Strings(record["facts"]),
                PreferenceIds = Strings(record["preferences"]),
                ClosedIds = Strings(record["closed"]),
            };
        }, cancellationToken);
    }

    public Task RecordAsync(MemoryOperationReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(RecordQuery, new Dictionary<string, object>
            {
                ["messageId"] = receipt.MessageId,
                ["operationId"] = receipt.OperationId,
                ["appliedAt"] = receipt.AppliedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["facts"] = receipt.FactIds.ToList(),
                ["preferences"] = receipt.PreferenceIds.ToList(),
                ["closed"] = receipt.ClosedIds.ToList(),
            }).ConfigureAwait(false);
            if (!await cursor.FetchAsync().ConfigureAwait(false))
                throw new InvalidOperationException($"Message '{receipt.MessageId}' is not stored; an applied-once write needs its message first.");
            if (cursor.Current["before"] is string before)
                throw new InvalidOperationException(
                    $"Message '{receipt.MessageId}' already carries the receipt of operation '{before}'; operation '{receipt.OperationId}' is not applied.");
            return true;
        }, cancellationToken);
    }

    private static IReadOnlyList<string> Strings(object? value) =>
        value is IEnumerable<object> items ? [.. items.OfType<string>()] : [];
}
