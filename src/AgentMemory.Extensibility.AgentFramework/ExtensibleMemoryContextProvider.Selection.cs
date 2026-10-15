using System.Text;
using AgentMemory.Extensibility.Context;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.AgentFramework;

public sealed partial class ExtensibleMemoryContextProvider
{
    private async Task<AIContext> SelectModuleContextAsync(AIContext core, ContextEnvelope envelope, InvokingContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var ids = ExtractIds(context.Session, context.Agent);
            // Compilation has disposed its ambient scopes. Judge resolution and observation must
            // run in this turn's application and the owner actually resolved by the compiler.
            using var scopes = BeginRetrievalScopes(ids.applicationId, envelope.Snapshot.Owner);
            var renderer = new SelectionRenderer(this, !string.IsNullOrEmpty(core.Instructions));
            var result = await _moduleTurnSelection!.SelectAsync(envelope, renderer, cancellationToken).ConfigureAwait(false);
            if (result.RenderedText.Length == 0) return core;
            return new AIContext
            {
                Instructions = (core.Instructions ?? string.Empty) + result.RenderedText,
                Messages = core.Messages,
                Tools = core.Tools,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            _logger.LogWarning("Module context selection failed; core context is unaffected.");
            return core;
        }
    }

    private sealed class SelectionRenderer(ExtensibleMemoryContextProvider provider, bool appendToInstructions)
        : IModuleCandidateRenderer
    {
        private readonly Dictionary<ModuleContextCandidate, AdmittedMemoryItem> _admitted = new(ReferenceEqualityComparer.Instance);

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
                if (builder.Length > 0 || appendToInstructions) builder.AppendLine().AppendLine();
                builder.Append(block);
            }
            return builder.ToString();
        }
    }
}
