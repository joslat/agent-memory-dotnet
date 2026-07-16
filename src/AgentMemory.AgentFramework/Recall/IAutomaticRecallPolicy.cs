namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// Pluggable, task-aware policy that decides -- before <c>Neo4jMemoryContextProvider</c> queries any
/// repository -- whether automatic recall is useful for the current turn, and if so, which memory
/// categories, retrieval limits, and ranking intent to use (#88). Runs deterministically inside
/// <c>ProvideAIContextAsync</c>/<c>BuildContextAsync</c>, before the <c>RecallRequest</c> is constructed.
/// Complements, and never replaces, the model-invokable memory tools (<c>Tools.MemoryToolFactory</c>).
/// </summary>
public interface IAutomaticRecallPolicy
{
    /// <summary>
    /// Decides the recall behavior for the current turn. Implementations must not require an additional
    /// model call -- an LLM-backed policy can be implemented externally for hosts that want that tradeoff.
    /// </summary>
    ValueTask<AutomaticRecallDecision> DecideAsync(
        AutomaticRecallContext context,
        CancellationToken cancellationToken = default);
}
