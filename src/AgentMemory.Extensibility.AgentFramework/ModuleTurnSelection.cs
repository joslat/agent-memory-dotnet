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
        return result;
    }
}
