using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Options;

#pragma warning disable AMEXT001 // Opt-in memory packs (Extensibility 0.13 §M).

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

    /// <summary>Recall with the opt-in memory packs' items beside the core context; without modules, the core result alone.</summary>
    public Task<CodingRecall> RecallWithModulesAsync(CodingIdentity identity, string session, string query,
        IReadOnlyList<AgentMemory.Extensibility.Context.ContextAnchor>? anchors = null, CancellationToken cancellationToken = default)
    {
        CodingInput.RequireText(session, 256, nameof(session));
        CodingInput.RequireText(query, 8000, nameof(query));
        content.CheckText(session);
        content.CheckText(query);
        anchors ??= [];
        if (anchors.Count > CodingAnchors.Maximum) throw new ArgumentException("Too many anchors.", nameof(anchors));
        foreach (var anchor in anchors) content.CheckText(anchor.Value);
        return scopes.RunAsync(identity, async (services, ct) =>
        {
            var request = new RecallRequest
            {
                SessionId = CodingInput.SessionId(identity, session), UserId = identity.OwnerId,
                Query = query, Options = options.Value.Recall with { Scope = MemoryScope.For(identity.OwnerId, false),
                    IncludeDiagnostics = receipts is not null || options.Value.Recall.IncludeDiagnostics },
            };
            if (services.GetService<AgentMemory.Extensibility.Context.ModuleContextSelection>() is not { } selection)
                return new CodingRecall(await services.GetRequiredService<IMemoryRecall>().RecallAsync(request, ct).ConfigureAwait(false), null);
            return await CodingModuleRecall.RecallAsync(services, selection, request, anchors, ct).ConfigureAwait(false);
        }, cancellationToken);
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
