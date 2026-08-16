using AgentMemory.AgentFramework.Security;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Records whether a promoted procedure actually reached the model's context on a given attempt (7.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because six consecutive runs of this benchmark measured a wiring gap and reported it
/// as a null result.</b> Storing a procedure, recalling it, and injecting it are three separate things,
/// and each has failed independently here: no provider on the arm at all; a trace written without the
/// task embedding every trace search requires; an outcome the formatter dropped. Every one of those
/// produces the same output — both arms identical, <c>SHOWS BENEFIT: False</c> — which is
/// indistinguishable from the feature honestly not helping.
/// </para>
/// <para>
/// So the arm is no longer trusted to be wired. The admission policy is the last gate a recalled item
/// passes before <c>MafTypeMapper</c> delimits it and hands it to the model, which makes it the one
/// place that can answer "was a procedure in the prompt" rather than "should a procedure have been in
/// the prompt". Counting here observes the property the measurement depends on instead of inferring it
/// from configuration that has been wrong three times.
/// </para>
/// <para>
/// It decides nothing. Every decision is delegated to the wrapped policy — the default one when none is
/// supplied — so witnessing a run cannot change its outcome. Only admitted items are counted: an item
/// the policy excluded was not injected, and counting it would restore exactly the false confidence
/// this class exists to remove.
/// </para>
/// </remarks>
internal sealed class ProceduralRecallWitness : IMemoryContextAdmissionPolicy
{
    /// <summary>The category <c>MafTypeMapper</c> evaluates a recalled reasoning trace under.</summary>
    private const string TraceCategory = "traces";

    private readonly IMemoryContextAdmissionPolicy _inner;
    private readonly List<string> _admitted = [];

    public ProceduralRecallWitness(IMemoryContextAdmissionPolicy? inner = null) =>
        _inner = inner ?? new DefaultMemoryContextAdmissionPolicy();

    /// <summary>Procedure blocks admitted into the context so far.</summary>
    public int AdmittedProcedureCount => _admitted.Count;

    /// <summary>The admitted procedure text, for reporting what the agent was actually told.</summary>
    public IReadOnlyList<string> AdmittedProcedures => _admitted;

    /// <inheritdoc/>
    public MemoryAdmissionDecision Evaluate(MemoryAdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var decision = _inner.Evaluate(context);
        if (decision.Include && string.Equals(context.Category, TraceCategory, StringComparison.Ordinal))
            _admitted.Add(context.Content);

        return decision;
    }
}
