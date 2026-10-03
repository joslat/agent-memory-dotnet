using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// PLAN 40.14 (F7): the read side of time. The server could supersede and invalidate (<see cref="MaintenanceTools"/>)
/// but not ask what was in force at a date, or what replaced what. Both reads already existed in the library
/// (<see cref="IMemoryRecall.RecallAsOfAsync"/> and <see cref="IMemoryHistoryService"/>); these tools expose them.
/// </summary>
[McpServerToolType]
internal sealed class HistoryTools
{
    [McpServerTool(Name = "memory_recall_as_of"), Description("Recall memory as it stood at a past moment: what was true then (asOf, the valid-time clock), optionally as the system knew it at another moment (systemAsOf, the transaction-time clock). Use it for 'where did I live in March?' or to reproduce what was believed before a correction. Returns the same sections as memory_search.")]
    public static async Task<string> MemoryRecallAsOf(
        IMemoryService memoryService,
        IOptions<AgentMemoryMcpOptions> options,
        IOptions<MemoryOptions> memoryOptions,
        [Description("The search query text")] string query,
        [Description("The moment to recall, ISO-8601 (for example 2026-03-15 or 2026-03-15T09:00:00Z)")] string asOf,
        [Description("Optional: the moment of knowledge, ISO-8601; defaults to asOf")] string? systemAsOf = null,
        [Description("Session identifier (optional, uses default if omitted)")] string? sessionId = null,
        [Description("User identifier (optional; required under strict owner isolation)")] string? userId = null,
        [Description("Maximum number of results per memory section")] int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMoment(asOf, out var validAt))
            return ToolJsonContext.Serialize(new { error = $"asOf '{asOf}' is not an ISO-8601 date or date-time" });
        DateTimeOffset? knownAt = null;
        if (!string.IsNullOrWhiteSpace(systemAsOf))
        {
            if (!TryParseMoment(systemAsOf, out var parsed))
                return ToolJsonContext.Serialize(new { error = $"systemAsOf '{systemAsOf}' is not an ISO-8601 date or date-time" });
            knownAt = parsed;
        }

        // The configured recall options, as memory_search does (25.2): only the section caps the caller named change.
        var request = new RecallRequest
        {
            SessionId = sessionId ?? options.Value.DefaultSessionId,
            UserId = userId,
            Query = query,
            Options = memoryOptions.Value.Recall with
            {
                MaxRecentMessages = maxResults,
                MaxRelevantMessages = maxResults,
                MaxEntities = maxResults,
                MaxPreferences = maxResults,
                MaxFacts = maxResults,
            },
        };

        var result = await memoryService.RecallAsOfAsync(request, validAt, knownAt, cancellationToken).ConfigureAwait(false);
        return ToolJsonContext.Serialize(new
        {
            asOf = validAt,
            systemAsOf = knownAt ?? validAt,
            result.TotalItemsRetrieved,
            result.Truncated,
            result.EstimatedTokenCount,
            context = McpMemoryProjection.Context(result.Context),
        });
    }

    [McpServerTool(Name = "memory_lineage"), Description("The lineage of a fact or preference: what it replaced and what replaced it, oldest first, each with when it was stated, when it held, when it was closed, and the messages it came from. Use it to answer 'what did this replace?' and 'why do you think so?'. Owner-scoped when userId is set.")]
    public static async Task<string> MemoryLineage(
        IMemoryHistoryService history,
        IMemoryIsolationPolicy isolationPolicy,
        [Description("Node kind: fact or preference")] string type,
        [Description("The id of any memory in the chain")] string id,
        [Description("User identifier for owner scoping (optional; required under strict owner isolation)")] string? userId = null,
        [Description("How many links to follow in each direction (1-50)")] int maxDepth = 10,
        CancellationToken cancellationToken = default)
    {
        var kind = (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "fact" => MemoryHistoryKind.Fact,
            "preference" or "pref" => MemoryHistoryKind.Preference,
            _ => (MemoryHistoryKind?)null,
        };
        if (kind is null)
            return ToolJsonContext.Serialize(new { error = $"unknown type '{type}' (expected fact or preference)" });

        // #100: fails closed under StrictMultiTenant before any read, as every tenant-facing tool does.
        var scope = isolationPolicy.ResolveReadScope(explicitScope: null, userId, nameof(MemoryLineage), MemoryOperationAccess.Tenant);
        var depth = Math.Clamp(maxDepth, 1, 50);

        var seen = new Dictionary<string, MemoryHistoryRecord>(StringComparer.Ordinal);
        var frontier = new List<(string Id, int Steps)> { (id, 0) };
        while (frontier.Count > 0)
        {
            var (next, steps) = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);
            if (seen.ContainsKey(next)) continue;
            var record = await ReadAsync(next).ConfigureAwait(false);
            if (record is null) continue;
            seen[next] = record;
            if (steps >= depth) continue;
            foreach (var linked in record.SupersedesIds.Concat(record.SupersededByIds))
            {
                if (!seen.ContainsKey(linked)) frontier.Add((linked, steps + 1));
            }
        }

        if (!seen.ContainsKey(id))
            return ToolJsonContext.Serialize(new { type = kind.Value.ToString().ToLowerInvariant(), id, found = false });

        var chain = seen.Values.OrderBy(record => record.CreatedAtUtc).ThenBy(record => record.Id, StringComparer.Ordinal).ToList();
        return ToolJsonContext.Serialize(new
        {
            type = kind.Value.ToString().ToLowerInvariant(),
            id,
            found = true,
            current = chain.Where(record => record.Status == MemoryHistoryStatus.Live && record.SupersededByIds.Count == 0)
                .Select(record => record.Id).ToArray(),
            chain = chain.Select(record => new
            {
                record.Id,
                record.Summary,
                status = record.Status.ToString().ToLowerInvariant(),
                statedAt = record.CreatedAtUtc,
                validFrom = record.ValidFromUtc,
                validUntil = record.ValidUntilUtc,
                closedAt = record.InvalidatedAtUtc,
                closedAs = record.ClosedAs,
                supersedes = record.SupersedesIds,
                supersededBy = record.SupersededByIds,
                sourceMessageIds = record.SourceMessageIds,
            }).ToArray(),
        });

        async Task<MemoryHistoryRecord?> ReadAsync(string memoryId)
        {
            var rows = await history.GetHistoryAsync(new MemoryHistoryQuery
            {
                Kind = kind,
                Id = memoryId,
                OwnerId = scope.OwnerId,
                IncludeShared = scope.IncludeShared,
                IncludeInvalidated = true,
                Limit = 1,
            }, cancellationToken).ConfigureAwait(false);
            return rows.FirstOrDefault(row => string.Equals(row.Id, memoryId, StringComparison.Ordinal));
        }
    }

    private static bool TryParseMoment(string? text, out DateTimeOffset moment) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out moment);
}
