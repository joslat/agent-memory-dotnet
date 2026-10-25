using System.Text;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.AgentFramework;

public sealed partial class ExtensibleMemoryContextProvider
{
    private sealed record PreparedSelection(ContextEnvelope Envelope, ModuleSelectionTurn Turn);

    /// <summary>
    /// The turn with module candidates judged in the core gate's one call: module views compile and are admitted while
    /// core recall searches, the gate awaits them after its own search, and the kept module items are rendered after the
    /// core block. Without a judging gate the module items keep their bounded fallback (Extensibility 0.12, R1-R3).
    /// </summary>
    private async Task<AIContext> ProvideWithCorePoolAsync(InvokingContext context, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<IReadOnlyList<MemoryGateCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var companions = new CompanionRecall(pending.Task);
        var renderer = new SelectionRenderer(this);
        // ORDER MATTERS. The base call runs its synchronous start first (it gives the turn's user messages their
        // provider ids); only then do module views read the same messages.
        var core = base.ProvideAIContextAsync(context, companions, cancellationToken).AsTask();
        var preparation = PrepareModulesAsync(context, renderer, cancellationToken);
        _ = preparation.ContinueWith(t => pending.TrySetResult(
                t.Status == TaskStatus.RanToCompletion && t.Result is { } prepared ? prepared.Turn.Companions : []),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        AIContext coreContext;
        try
        {
            coreContext = await core.ConfigureAwait(false);
        }
        finally
        {
            // The module work is never left running unobserved, whatever core did.
            try { await preparation.ConfigureAwait(false); } catch { }
        }
        var selection = await preparation.ConfigureAwait(false);
        if (selection is null) return coreContext;
        try
        {
            var ids = ExtractIds(context.Session, context.Agent);
            // Compilation has disposed its ambient scopes. Observation must run in this turn's application and the
            // owner actually resolved by the compiler.
            using var scopes = BeginRetrievalScopes(ids.applicationId, selection.Envelope.Snapshot.Owner);
            renderer.AppendToInstructions = !string.IsNullOrEmpty(coreContext.Instructions);
            var result = await _moduleTurnSelection!.CompleteAsync(selection.Envelope, selection.Turn,
                companions.Decisions, companions.AnsweredBy, companions.OfferedToJudge, cancellationToken).ConfigureAwait(false);
            if (result.RenderedText.Length == 0) return coreContext;
            return new AIContext
            {
                Instructions = (coreContext.Instructions ?? string.Empty) + result.RenderedText,
                Messages = coreContext.Messages,
                Tools = coreContext.Tools,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            _logger.LogWarning("Module context selection failed; core context is unaffected.");
            return coreContext;
        }
    }

    /// <summary>Compiles the module views and prepares their candidates; null when there is nothing to add.</summary>
    private async Task<PreparedSelection?> PrepareModulesAsync(InvokingContext context, SelectionRenderer renderer,
        CancellationToken cancellationToken)
    {
        var envelope = await CompileModulesAsync(context, cancellationToken).ConfigureAwait(false);
        if (envelope is null) return null;
        try
        {
            return new PreparedSelection(envelope, _moduleTurnSelection!.PrepareForCorePool(envelope, renderer, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            _logger.LogWarning("Module context selection failed; core context is unaffected.");
            return null;
        }
    }

    private sealed class SelectionRenderer(ExtensibleMemoryContextProvider provider) : IModuleCandidateRenderer
    {
        private readonly Dictionary<ModuleContextCandidate, AdmittedMemoryItem> _admitted = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether the module block follows core instructions, and so starts with their separator.</summary>
        public bool AppendToInstructions { get; set; }

        public bool Admit(ModuleContextCandidate candidate)
        {
            var item = provider.AdmitMemoryItem(candidate.Binding.SectionType, candidate.Text);
            if (item is null) return false;
            _admitted.Add(candidate, item);
            return true;
        }

        public string Render(IReadOnlyList<ModuleContextCandidate> candidates)
        {
            var builder = new StringBuilder();
            foreach (var group in candidates.GroupBy(c => c.Binding))
            {
                var block = provider.RenderAdmittedItems(group.Key.SectionType, group.Select(c => _admitted[c]));
                if (block.Length == 0) continue;
                if (builder.Length > 0 || AppendToInstructions) builder.AppendLine().AppendLine();
                builder.Append(block);
            }
            return builder.ToString();
        }
    }
}
