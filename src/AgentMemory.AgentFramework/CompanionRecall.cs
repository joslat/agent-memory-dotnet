using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.AgentFramework;

/// <summary>
/// One turn's companions for the core retrieval gate and, once core recall returns, what the gate decided about them
/// (Extensibility 0.12, R1). Passed to <see cref="Neo4jMemoryContextProvider"/>'s protected companion overload of
/// <c>ProvideAIContextAsync</c>; a subclass such as the extensible provider keeps it for the turn and reads the decisions back.
/// </summary>
/// <remarks>
/// One instance per turn, owned by the caller, so concurrent sessions on one provider never share decisions.
/// </remarks>
public sealed class CompanionRecall
{
    /// <summary>The companions, gathered or still being gathered while core recall searches.</summary>
    public CompanionRecall(Task<IReadOnlyList<MemoryGateCandidate>> candidates) =>
        Request = new RecallCompanions(candidates ?? throw new ArgumentNullException(nameof(candidates)));

    /// <summary>What core recall carries to the gate.</summary>
    internal RecallCompanions Request { get; }

    /// <summary>The companion candidates handed to core recall.</summary>
    public Task<IReadOnlyList<MemoryGateCandidate>> Candidates => Request.Candidates;

    /// <summary>Whether core recall ran with the companions this turn (false: no user message, the recall policy skipped
    /// recall, or recall failed).</summary>
    public bool Offered { get; private set; }

    /// <summary>The gate's probability for each companion it judged; null when no gate judged them.</summary>
    public IReadOnlyDictionary<string, double>? Decisions { get; private set; }

    /// <summary>Which judges answered, when the gate judged the companions.</summary>
    public string? AnsweredBy { get; private set; }

    /// <summary>Whether a gate call carried the companions, including a call that then failed.</summary>
    public bool OfferedToJudge { get; private set; }

    internal void Report(MemoryContext context)
    {
        Offered = true;
        Decisions = context.CompanionDecisions;
        OfferedToJudge = context.Metadata.TryGetValue(RecallCompanions.OfferedMetadataKey, out var sent) && sent is int count && count > 0;
        AnsweredBy = context.CompanionDecisions is null ? null
            : context.Metadata.TryGetValue(RecallCompanions.JudgedByMetadataKey, out var judges) ? judges as string : null;
    }
}
