using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpHost.Coding;

/// <summary>Reads current, owner-checked evidence within the already bound project database.</summary>
internal sealed class CodingEvidenceReader(CodingOperationScope scopes)
{
    public Task<CodingEvidenceItem> ReadAsync(CodingIdentity identity, CodingEvidenceSelection request,
        CodingRecallItem selected, CancellationToken cancellationToken) => scopes.RunAsync(identity, async (services, ct) =>
    {
        var missing = Unavailable(request);
        var kind = Kind(request.Section);
        if (kind is not null)
        {
            var rows = await services.GetRequiredService<IMemoryHistoryService>().GetHistoryAsync(new()
            {
                Id = request.ItemId, Kind = kind, OwnerId = identity.OwnerId, IncludeShared = false,
                IncludeInvalidated = false, Limit = 1,
            }, ct).ConfigureAwait(false);
            var row = rows.SingleOrDefault();
            var now = DateTimeOffset.UtcNow;
            if (row is null || row.Id != request.ItemId || row.Kind != kind || row.OwnerId != identity.OwnerId ||
                row.Status != MemoryHistoryStatus.Live || row.InvalidatedAtUtc is not null || row.SupersededByIds.Count != 0 ||
                row.MergedIntoId is not null || (kind == MemoryHistoryKind.Fact &&
                    (row.ValidFromUtc > now || row.ValidUntilUtc <= now))) return missing;
            if (request.SourceMessageId is { } source)
            {
                if (!selected.SourceMessageIds.Contains(source, StringComparer.Ordinal) ||
                    !row.SourceMessageIds.Contains(source, StringComparer.Ordinal)) return missing;
                return await MessageAsync(source, services, identity, request, ct).ConfigureAwait(false);
            }
            return new(request.Section, request.ItemId, null, "available", row.Summary, row.CreatedAtUtc,
                BoundedSources(row.SourceMessageIds), null);
        }
        if (request.SourceMessageId is not null) return missing;
        if (request.Section is "recentMessages" or "relevantMessages")
            return await MessageAsync(request.ItemId, services, identity, request, ct).ConfigureAwait(false);
        if (request.Section == "similarTraces")
        {
            // The supported trace lifecycle has deletion, not fact-style soft closure. Read again; never use a cached body.
            var trace = await services.GetRequiredService<IReasoningTraceRepository>().GetByIdAsync(request.ItemId, ct).ConfigureAwait(false);
            if (trace is null || trace.TraceId != request.ItemId || trace.OwnerId != identity.OwnerId) return missing;
            return new(request.Section, request.ItemId, null, "available",
                trace.Task + (string.IsNullOrWhiteSpace(trace.Outcome) ? "" : "\nReported outcome: " + trace.Outcome),
                trace.StartedAtUtc, [], null);
        }
        return missing;
    }, cancellationToken);

    public static CodingEvidenceItem Unavailable(CodingEvidenceSelection request) =>
        new(request.Section, request.ItemId, request.SourceMessageId, "unavailable", null, null, [], null);

    private static MemoryHistoryKind? Kind(string section) => section switch
    {
        "relevantFacts" or "dueFacts" or "expiringFacts" => MemoryHistoryKind.Fact,
        "relevantEntities" => MemoryHistoryKind.Entity,
        "relevantPreferences" => MemoryHistoryKind.Preference,
        _ => null,
    };
    private static async Task<CodingEvidenceItem> MessageAsync(string id, IServiceProvider services, CodingIdentity identity,
        CodingEvidenceSelection request, CancellationToken cancellationToken)
    {
        var message = await services.GetRequiredService<IMessageRepository>().GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (message is null || message.MessageId != id || message.InvalidatedAtUtc is not null) return Unavailable(request);
        var conversation = await services.GetRequiredService<IConversationRepository>()
            .GetByIdAsync(message.ConversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null || conversation.ConversationId != message.ConversationId ||
            conversation.UserId != identity.OwnerId || conversation.SessionId != message.SessionId) return Unavailable(request);
        var commit = Assertion("coding_commit", 64);
        if (commit is not null && (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))) commit = null;
        var source = Assertion("coding_source", 512);
        return new(request.Section, request.ItemId, request.SourceMessageId, "available", message.Content, message.TimestampUtc,
            [id], commit is null && source is null ? null : new(commit, source));
        string? Assertion(string key, int maximum)
        {
            if (!message.Metadata.TryGetValue(key, out var value)) return null;
            var text = value switch
            {
                string raw => raw,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
                _ => null,
            };
            return text is not null && text.Length <= maximum && !text.Any(char.IsControl) ? text : null;
        }
    }
    private static IReadOnlyList<string> BoundedSources(IReadOnlyList<string> sources) =>
        sources.Take(4).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 256 && !s.Any(char.IsControl)).ToArray();
}
