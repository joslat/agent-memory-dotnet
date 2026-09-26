using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Services;
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
        if (options.ExtractInBackground && background is not null)
        {
            // The caller's ambient owner and store scope (AsyncLocal) travel with the work, so it writes
            // where the turn did even on another thread or scheduler.
            var context = ExecutionContext.Capture();
            if (background.TryEnqueue(request.SessionId ?? string.Empty,
                    token => context is null
                        ? RunAsync(memoryService, request, logger, token, queued: true)
                        : InContext(context, () => RunAsync(memoryService, request, logger, token, queued: true))))
            {
                return;
            }
        }

        await RunAsync(memoryService, request, logger, cancellationToken, queued: false).ConfigureAwait(false);
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

    private static Task InContext(ExecutionContext context, Func<Task> work)
    {
        Task? started = null;
        ExecutionContext.Run(context, _ => started = work(), null);
        return started!;
    }
}
