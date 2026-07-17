namespace AgentMemory.Nams.Persistence;

/// <summary>
/// Thrown by <see cref="INamsPersistenceService.PersistTurnAsync"/> only when
/// <c>NamsOptions.PersistenceFailureMode</c> is <c>FailInvocation</c> and the outcome was
/// <see cref="NamsPersistenceOutcome.Failed"/> or <see cref="NamsPersistenceOutcome.UnknownWriteOutcome"/>.
/// Engineering plan §7 Phase 5: in <c>FailInvocation</c> mode "the persistence exception is propagated
/// after the model response" -- calling <c>PersistTurnAsync</c> only after the model response has already
/// been produced (a Phase 6 orchestration decision) is what gives that ordering, not anything this type does.
/// </summary>
public sealed class NamsPersistenceFailedException : Exception
{
    public NamsPersistenceResult Result { get; }

    public NamsPersistenceFailedException(NamsPersistenceResult result)
        : base(result.FailureReason ?? "NAMS message persistence failed.")
    {
        Result = result;
    }
}
