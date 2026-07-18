using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;

namespace AgentMemory.Tests.Unit.Nams;

/// <summary>
/// Base fake for <see cref="INamsClient"/> in unit tests: every member throws <see cref="NotSupportedException"/>
/// by default (virtual, so a derived fake overrides only the operation(s) its test actually exercises).
/// Consolidates what had drifted into 5 near-identical hand-rolled <c>INamsClient</c> implementations, each
/// independently re-declaring the same ~18-throw-statement boilerplate for every interface member the test
/// didn't care about -- a Phase 10-review finding (INamsClient grew from 7 to 20 members across Phase 9-10i,
/// and each addition required a mechanical edit to all 5 files).
/// </summary>
internal class ThrowingNamsClientStub : INamsClient
{
    public virtual Task<NamsConversation> CreateConversationAsync(
        string? userId, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsContext> GetContextAsync(string conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsMessage>> AddMessagesAsync(
        string conversationId, IReadOnlyList<NamsMessageInput> messages, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsEntity>> SearchEntitiesAsync(
        string query, string? type, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsEntity>> ListEntitiesAsync(int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsMessage>> SearchMessagesAsync(
        string conversationId, string query, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsObservation>> GetObservationsAsync(
        string conversationId, int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsEntityFeedbackResult> SetEntityFeedbackAsync(
        string entityId, double? userScore, bool? confirmed, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsEntityGraph> GetEntityGraphAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsGraphExpansion> ExpandGraphAsync(
        string nodeId, IReadOnlyList<string> loadedIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsReasoningStep> RecordReasoningStepAsync(
        string conversationId, string reasoning, string actionTaken, string? result, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<IReadOnlyList<NamsReasoningStep>> ListReasoningStepsAsync(string conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsToolCall> RecordToolCallAsync(
        string? stepId, string toolName, string input, string? output, string? status, int? durationMs,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsReasoningTrace> GetReasoningTraceAsync(string conversationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsEntityProvenance> GetEntityProvenanceAsync(string entityId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public virtual Task<NamsQueryResult> ExecuteCypherQueryAsync(
        string cypher, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
