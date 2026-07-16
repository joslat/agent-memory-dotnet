namespace AgentMemory.AgentFramework.Security;

/// <summary>
/// Evaluates whether a candidate recalled-memory block is safe/appropriate to inject into the model
/// context, before <c>MafTypeMapper.ToContextMessages</c> delimits and adds it (#92 Phase 2). Runs
/// independently of, and after, an <c>IAutomaticRecallPolicy</c> (#88) decision -- retrieval selection and
/// admission are separate responsibilities.
/// </summary>
public interface IMemoryContextAdmissionPolicy
{
    /// <summary>
    /// Decides whether to admit one recalled-memory block. Deliberately synchronous: the built-in
    /// detector is pure, CPU-bound pattern matching with no I/O. This is not a complete prompt-injection
    /// detector -- it is a lightweight, deterministic heuristic (see issue #92's non-goals).
    /// </summary>
    MemoryAdmissionDecision Evaluate(MemoryAdmissionContext context);
}
