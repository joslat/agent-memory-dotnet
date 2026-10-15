using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.AgentFramework;

[Experimental("AMEXT001")]
internal sealed record ModuleTurnIngestionBinding(Func<IServiceProvider, IModuleIngestion> Create);

/// <summary>Explicit host binding from persisted turns to an authorized module ingestion composition.</summary>
/// <remarks>Created by AddAgentMemoryFrameworkModuleIngestion. It does not grant authorization:
/// the ingestion pipeline must resolve isolation and validate every proposal and stored source.</remarks>
[Experimental("AMEXT001")]
public sealed class ModuleTurnIngestion
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ModuleTurnIngestionBinding _binding;
    private readonly bool _background;
    private readonly ILogger<ModuleTurnIngestion> _logger;
    private readonly IBackgroundExtraction? _queue;

    internal ModuleTurnIngestion(IServiceProvider services, IServiceScopeFactory scopeFactory,
        ModuleTurnIngestionBinding binding, AgentFrameworkOptions options,
        ILogger<ModuleTurnIngestion> logger, IBackgroundExtraction? queue)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _binding = binding;
        _background = options.ExtractInBackground;
        _logger = logger;
        _queue = queue;
    }

    internal async Task IngestAsync(PersistedMemoryTurn turn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = turn.Messages.Where(m => m.Role is "user" or "assistant")
            .Select(m => new IngestedMessage { Id = m.Id, Role = m.Role, Text = m.Text }).ToArray();
        if (messages.Length == 0) return;
        var request = new IngestionRequest
        {
            Owner = turn.Owner, SessionId = turn.SessionId, AtUtc = turn.AtUtc,
            Messages = Array.AsReadOnly(messages), Trigger = IngestionTrigger.EveryTurn
        };
        if (_background && _queue is not null)
        {
            var key = string.IsNullOrWhiteSpace(turn.Owner) ? "session:" + turn.SessionId : "owner:" + turn.Owner;
            // Capture singleton dependencies and immutable input, never this scoped handler or its provider.
            var factory = _scopeFactory;
            var binding = _binding;
            var logger = _logger;
            var applicationId = turn.ApplicationId;
            if (_queue.TryEnqueue(key, token => RunInFreshScopeAsync(factory, binding, logger, request, applicationId, token)))
                return;
            // Rejection during shutdown must not let this turn overtake already accepted work.
            await _queue.WhenIdleAsync(key, cancellationToken).ConfigureAwait(false);
        }
        await RunAsync(_services, _binding, _logger, request, turn.ApplicationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunInFreshScopeAsync(IServiceScopeFactory factory, ModuleTurnIngestionBinding binding,
        ILogger<ModuleTurnIngestion> logger, IngestionRequest request, string? applicationId, CancellationToken cancellationToken)
    {
        var scope = factory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await RunAsync(scope.ServiceProvider, binding, logger, request, applicationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunAsync(IServiceProvider services, ModuleTurnIngestionBinding binding,
        ILogger<ModuleTurnIngestion> logger, IngestionRequest request, string? applicationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = services.GetService<IMemoryStoreContext>();
        var writableStore = store as IWritableMemoryStoreContext ?? services.GetService<IWritableMemoryStoreContext>();
        using var storeScope = writableStore?.BeginStoreScope(applicationId);
        // A supplied read-only store context must also observe the requested selection. Never silently
        // ingest into the default store after a request-scoped application selection has disappeared.
        if ((store is not null && !string.Equals(store.ApplicationId, applicationId, StringComparison.Ordinal)) ||
            (store is null && writableStore is null && applicationId is not null))
            throw new InvalidOperationException("The captured module ingestion store scope cannot be established.");
        var owner = services.GetService<IWritableMemoryOwnerContext>();
        using var ownerScope = owner?.BeginOwnerScope(request.Owner);
        var pipeline = binding.Create(services) ?? throw new InvalidOperationException("The module ingestion factory returned null.");
        var report = await pipeline.IngestAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (report.Denied)
            logger.LogWarning("Module ingestion was denied by isolation policy.");
        foreach (var result in report.Providers)
            if (result.Status != ProviderIngestionStatus.Succeeded)
                logger.LogWarning("Module ingestion provider {ProviderId} completed with {Status}.", result.ProviderId, result.Status);
    }
}
