namespace AgentMemory.Nams.Identity;

/// <summary>Result of <see cref="INamsConversationResolver.ResolveAsync"/>.</summary>
/// <param name="NamsConversationId">The resolved NAMS conversation ID -- either newly created or reused.</param>
/// <param name="WasCreated"><c>true</c> if this call created the NAMS conversation; <c>false</c> if an
/// existing mapping was reused (including the reconciliation path after a lost creation race).</param>
public sealed record NamsConversationResolutionResult(string NamsConversationId, bool WasCreated);
