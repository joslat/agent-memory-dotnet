using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

namespace AgentMemory.McpHost.Coding;

/// <summary>Operations shared by local HTTP hooks and coding MCP tools.</summary>
internal sealed class CodingMemoryOperations(CodingOperationScope scopes, IOptions<MemoryOptions> options, CodingContentPolicy content,
    CodingRecallReceipts? receipts = null)
{
    public Task<RecallResult> RecallAsync(CodingIdentity identity, string session, string query,
        CancellationToken cancellationToken = default)
    {
        CodingInput.RequireText(session, 256, nameof(session));
        CodingInput.RequireText(query, 8000, nameof(query));
        content.CheckText(session);
        content.CheckText(query);
        return scopes.RunAsync(identity, (services, ct) => services.GetRequiredService<IMemoryRecall>()
            .RecallAsync(new RecallRequest
            {
                SessionId = CodingInput.SessionId(identity, session), UserId = identity.OwnerId,
                Query = query, Options = options.Value.Recall with { Scope = MemoryScope.For(identity.OwnerId, false),
                    IncludeDiagnostics = receipts is not null || options.Value.Recall.IncludeDiagnostics },
            }, ct), cancellationToken);
    }

    public Task<bool> ForgetAsync(CodingIdentity identity, MemoryHistoryKind kind, string id,
        CancellationToken cancellationToken = default)
    {
        CodingInput.RequireText(id, 256, nameof(id));
        content.CheckText(id);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown memory kind.", nameof(kind));
        return scopes.RunAsync(identity, (services, ct) =>
        {
            var memory = services.GetRequiredService<ILongTermMemoryService>();
            var scope = MemoryScope.For(identity.OwnerId, false);
            return kind switch
            {
                MemoryHistoryKind.Fact => memory.InvalidateFactAsync(id, scope, ct),
                MemoryHistoryKind.Entity => memory.InvalidateEntityAsync(id, scope, ct),
                MemoryHistoryKind.Preference => memory.InvalidatePreferenceAsync(id, scope, ct),
                _ => throw new ArgumentException("Unknown memory kind.", nameof(kind)),
            };
        }, cancellationToken);
    }

    public Task<IReadOnlyList<MemoryHistoryRecord>> HistoryAsync(CodingIdentity identity, string? id = null,
        CancellationToken cancellationToken = default)
    {
        if (id is not null)
        {
            CodingInput.RequireText(id, 256, nameof(id));
            content.CheckText(id);
        }
        return scopes.RunAsync(identity, (services, ct) => services.GetRequiredService<IMemoryHistoryService>()
            .GetHistoryAsync(new MemoryHistoryQuery
            {
                Id = id, OwnerId = identity.OwnerId, IncludeShared = false, IncludeInvalidated = true, Limit = 50,
            }, ct), cancellationToken);
    }

    public Task<WorkingMemoryBlock?> ProfileAsync(CodingIdentity identity,
        CancellationToken cancellationToken = default) => scopes.RunAsync(identity, (services, ct) =>
            services.GetRequiredService<IWorkingMemoryService>().GetAsync(identity.OwnerId, ct), cancellationToken);
}
