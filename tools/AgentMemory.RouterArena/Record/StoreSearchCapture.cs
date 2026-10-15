using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.RouterArena.Record;

internal sealed record SearchCaptureResult
{
    public required string CaseId { get; init; }
    public required int Session { get; init; }
    public required string OwnerId { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }
    public required RecallRequest Recall { get; init; }
    public MemoryGateRequest? Request { get; init; }
    public IReadOnlyList<GateCandidates.Entry> Entries { get; init; } = [];
    public MemoryContext? Context { get; init; }
    public string? Error { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>Captures the actual pre-judge context from the registered assembler in the writer's isolated store.</summary>
internal sealed class StoreSearchCapture(IMemoryContextAssembler inner, MemoryOptions options, IClock clock)
{
    public static void Register(IServiceCollection services, MemoryOptions options)
    {
        var original = services.Last(d => d.ServiceType == typeof(IMemoryContextAssembler));
        // The recorder registers the standard factory, before AddAgentMemoryGate decorates it.
        var factory = original.ImplementationFactory ?? throw new InvalidOperationException("Expected the original assembler factory.");
        services.AddScoped(sp => new StoreSearchCapture((IMemoryContextAssembler)factory(sp), options, sp.GetRequiredService<IClock>()));
    }

    public async Task<SearchCaptureResult> CaptureAsync(SearchCaptureQuery query, string sessionId, string ownerId,
        int wideLimit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(sessionId) || wideLimit is < 1 or > 512)
            throw new ArgumentException("A scoped capture and bounded wide limit are required.");
        var at = clock.UtcNow;
        var gate = new GatedMemoryContextAssembler(inner, new NoJudge(),
            Options.Create(new MemoryGateOptions { Mode = MemoryGateMode.Everything, WideLimit = wideLimit }),
            NullLogger<GatedMemoryContextAssembler>.Instance, Options.Create(options), clock);
        var basis = options.Recall with { Scope = MemoryScope.For(ownerId, includeShared: false), IncludeDiagnostics = true };
        var recall = new RecallRequest
        {
            SessionId = sessionId, UserId = ownerId, Query = query.Question,
            Options = query.Historical ? basis : gate.Wide(basis, new MemoryGateOptions { WideLimit = wideLimit }),
        };
        var row = new SearchCaptureResult
        {
            CaseId = query.Id, Session = query.Session, OwnerId = ownerId, CapturedAtUtc = at,
            AsOfUtc = query.Historical ? at : null, Recall = recall,
        };
        var watch = Stopwatch.StartNew();
        try
        {
            var context = query.Historical
                ? await inner.AssembleContextAsOfAsync(recall, at, at, cancellationToken).ConfigureAwait(false)
                : await gate.AssembleContextAsync(recall, cancellationToken).ConfigureAwait(false);
            var entries = GateCandidates.Of(context);
            var partial = context.LatencyBudgetExceeded || new[]
            {
                context.RecentMessages.Diagnostics, context.RelevantMessages.Diagnostics,
                context.RelevantFacts.Diagnostics, context.DueFacts.Diagnostics, context.ExpiringFacts.Diagnostics,
                context.RelevantEntities.Diagnostics, context.RelevantPreferences.Diagnostics,
                context.RelevantRelationships.Diagnostics, context.SimilarTraces.Diagnostics,
            }.Any(d => d?.TimedOut == true);
            return row with
            {
                Context = context, Entries = entries,
                Error = partial ? "PartialSearchLatencyBudgetExceeded" : null,
                Request = query.Historical || partial ? null : new MemoryGateRequest(query.Question,
                    GatedMemoryContextAssembler.Conversation(context, query.Question), at, entries.Select(e => e.Candidate).ToArray()),
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return row with { Error = ex.GetType().Name, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds }; }
    }

    private sealed class NoJudge : IMemoryGate
    {
        public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Search capture must never invoke a relevance judge.");
    }
}
