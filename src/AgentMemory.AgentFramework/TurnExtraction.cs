using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Services;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMemory.AgentFramework;

/// <summary>
/// The one place a turn's extraction is run, by every Agent Framework entry point that extracts on
/// persist (the context provider, the chat-history provider, the facade), so inline and background
/// behave the same whichever one a host uses.
/// </summary>
internal static class TurnExtraction
{
    /// <summary>
    /// Extracts the turn now, or queues it (<see cref="AgentFrameworkOptions.ExtractInBackground"/>). A
    /// failure is logged and recorded on the current span, never thrown: the turn itself succeeded.
    /// </summary>
    internal static async Task ExtractAsync(
        IMemoryService memoryService,
        ExtractionRequest request,
        AgentFrameworkOptions options,
        IBackgroundExtraction? background,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Here, not at each entry point, so every one of them honours it (review round 3: two did not).
        if (options.ExtractFromUserMessagesOnly)
        {
            var said = request.Messages.Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)).ToList();
            if (said.Count == 0) return;
            request = request with { Messages = said };
        }

        if (options.ExtractInBackground && background is not null)
        {
            // The caller's ambient owner and store scope (AsyncLocal) travel with the work, so it writes
            // where the turn did even on another thread or scheduler. The owner also travels explicitly, as
            // request.UserId. The trace does not: queued work is its own trace, linked to the turn's.
            var context = ExecutionContext.Capture();
            var turn = Activity.Current?.Context;
            var scopes = (background as BackgroundExtractionQueue)?.Scopes;
            if (background.TryEnqueue(OrderingKey(request.UserId, request.SessionId),
                    token => context is null
                        ? RunQueuedAsync(scopes, memoryService, request, logger, turn, token)
                        : InContext(context, () => RunQueuedAsync(scopes, memoryService, request, logger, turn, token))))
            {
                return;
            }
        }

        await RunAsync(memoryService, request, logger, cancellationToken, queued: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Queued work: its services come from a fresh scope when the default queue has one (the turn's scope
    /// has usually ended, and a scoped disposable dependency would be disposed), and its span starts a new
    /// trace linked to the turn instead of a child of a span that has already ended.
    /// </summary>
    private static async Task RunQueuedAsync(
        IServiceScopeFactory? scopes, IMemoryService turnsMemoryService, ExtractionRequest request, ILogger logger,
        ActivityContext? turn, CancellationToken cancellationToken)
    {
        Activity.Current = null;
        using var root = AgentMemoryDiagnostics.Source.StartActivity("memory.store.extract.background", ActivityKind.Internal,
            parentContext: default, links: turn is { } link ? [new ActivityLink(link)] : null);
        if (scopes is null)
        {
            await RunAsync(turnsMemoryService, request, logger, cancellationToken, queued: true).ConfigureAwait(false);
            return;
        }
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            await RunAsync(memoryService, request, logger, cancellationToken, queued: true).ConfigureAwait(false);
        }
    }

    private static async Task RunAsync(
        IMemoryService memoryService, ExtractionRequest request, ILogger logger, CancellationToken cancellationToken, bool queued)
    {
        using var span = AgentMemoryDiagnostics.Source.StartActivity("memory.store.extract");
        span?.SetTag("memory.extract.source_messages", request.Messages.Count);
        span?.SetTag("memory.extract.background", queued);
        try
        {
            await memoryService.ExtractAndPersistAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Swallowed (the turn succeeded), but not invisible: the span says so.
            MemoryTelemetry.RecordException(System.Diagnostics.Activity.Current, ex);
            logger.LogWarning(ex, "Extraction failed for session {SessionId}; messages were persisted.", request.SessionId);
        }
    }

    /// <summary>
    /// One owner's turns are learned one at a time, in order (and never write that owner's graph
    /// concurrently); different owners in parallel. Without an owner, the session.
    /// </summary>
    internal static string OrderingKey(string? userId, string? sessionId) =>
        !string.IsNullOrWhiteSpace(userId) ? "owner:" + userId : "session:" + (sessionId ?? string.Empty);

    /// <summary>
    /// The next-turn guard: before recall, wait (at most <paramref name="budget"/>) for this owner's pending
    /// extraction, so a fact stated in the last turn can be recalled in this one. Returns the time waited.
    /// </summary>
    internal static async Task<TimeSpan> WaitForPendingAsync(
        IBackgroundExtraction? background, AgentFrameworkOptions options, string? userId, string? sessionId,
        CancellationToken cancellationToken)
    {
        if (background is null || !options.ExtractInBackground || options.RecallWaitsForPendingExtraction <= TimeSpan.Zero)
            return TimeSpan.Zero;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await background.WhenIdleAsync(OrderingKey(userId, sessionId), cancellationToken)
                .WaitAsync(options.RecallWaitsForPendingExtraction, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Recall goes ahead without what is still being learned: the answer does not wait longer.
        }
        return Stopwatch.GetElapsedTime(started);
    }

    private static Task InContext(ExecutionContext context, Func<Task> work)
    {
        Task? started = null;
        ExecutionContext.Run(context, _ => started = work(), null);
        return started!;
    }
}
