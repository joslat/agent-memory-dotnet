using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Context;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.AgentFramework;

/// <summary>Observes one request's module selection without storing a shared mutable last result.</summary>
/// <remarks>Envelopes and results contain private text and provenance. Scope storage/access to the
/// resolved owner and host application; do not export their contents as unrestricted telemetry.</remarks>
[Experimental("AMEXT001")]
public interface IModuleSelectionObserver
{
    /// <summary>Receives the compilation envelope and the exact selected module addition.</summary>
    Task ObserveAsync(ContextEnvelope envelope, ModuleSelectionResult result, CancellationToken cancellationToken = default);
}

/// <summary>Explicit scoped native selection and optional result observation.</summary>
[Experimental("AMEXT001")]
public sealed class ModuleTurnSelection
{
    private readonly ModuleContextSelection _selection;
    private readonly ILogger<ModuleTurnSelection> _logger;
    private readonly Func<IModuleSelectionObserver?>? _observerFactory;

    /// <summary>Creates a host selection path; supplying a selector does not change its judge opt-in.</summary>
    public ModuleTurnSelection(ModuleContextSelection selection, ILogger<ModuleTurnSelection> logger,
        Func<IModuleSelectionObserver?>? observerFactory = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observerFactory = observerFactory;
    }

    /// <summary>Selects module content and reports its outcome to the optional scoped observer.</summary>
    public async Task<ModuleSelectionResult> SelectAsync(ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        var result = await _selection.SelectAsync(envelope, renderer, cancellationToken).ConfigureAwait(false);
        await ObserveAsync(envelope, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Prepares a selection whose candidates the core gate judges with core memories (Extensibility 0.12, R1).</summary>
    public ModuleSelectionTurn PrepareForCorePool(ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        CancellationToken cancellationToken = default) => _selection.PrepareForCorePool(envelope, renderer, cancellationToken);

    /// <summary>Completes a pooled selection with the core gate's decisions and reports it to the optional observer.</summary>
    public async Task<ModuleSelectionResult> CompleteAsync(ContextEnvelope envelope, ModuleSelectionTurn turn,
        IReadOnlyDictionary<string, double>? decisions, string? answeredBy, bool offeredToJudge = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var result = turn.Complete(decisions, answeredBy, offeredToJudge, cancellationToken);
        await ObserveAsync(envelope, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task ObserveAsync(ContextEnvelope envelope, ModuleSelectionResult result, CancellationToken cancellationToken)
    {
        if (_observerFactory is not null)
        {
            try
            {
                var observer = _observerFactory.Invoke();
                if (observer is not null)
                    await observer.ObserveAsync(envelope, result, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                _logger.LogWarning("Module selection observation failed; selected context is unaffected.");
            }
        }
    }
}
