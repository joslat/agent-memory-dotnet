using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Nams.Client;
using AgentMemory.Nams.Domain;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Nams.Persistence;

/// <summary>
/// Implements the engineering plan's §7 Phase 5 algorithm: build one ordered bulk payload (user messages,
/// then assistant messages), submit exactly one <see cref="INamsClient.AddMessagesAsync"/> call (no retry --
/// matches Phase 2's own non-retry-for-writes stance), classify the outcome, and apply
/// <c>NamsOptions.PersistenceFailureMode</c> to decide whether a failure escalates to an exception.
/// </summary>
internal sealed class NamsPersistenceService : INamsPersistenceService
{
    private readonly INamsClient _namsClient;
    private readonly NamsOptions _options;
    private readonly ILogger<NamsPersistenceService> _logger;
    private readonly NamsMetrics _metrics;

    public NamsPersistenceService(
        INamsClient namsClient, IOptions<NamsOptions> options, ILogger<NamsPersistenceService> logger, NamsMetrics metrics)
    {
        _namsClient = namsClient;
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<NamsPersistenceResult> PersistTurnAsync(
        string namsConversationId,
        IReadOnlyList<NamsMessageToPersist> userMessages,
        IReadOnlyList<NamsMessageToPersist> assistantMessages,
        CancellationToken cancellationToken)
    {
        var ordered = new List<NamsMessageInput>(userMessages.Count + assistantMessages.Count);
        ordered.AddRange(userMessages.Select(m => new NamsMessageInput(m.Content, "user")));
        ordered.AddRange(assistantMessages.Select(m => new NamsMessageInput(m.Content, "assistant")));

        if (ordered.Count == 0)
            return new NamsPersistenceResult { Outcome = NamsPersistenceOutcome.Persisted };

        NamsPersistenceResult result;
        try
        {
            var persisted = await _namsClient.AddMessagesAsync(namsConversationId, ordered, cancellationToken).ConfigureAwait(false);
            result = new NamsPersistenceResult
            {
                Outcome = NamsPersistenceOutcome.Persisted,
                PersistedMessageIds = persisted.Select(m => m.Id).ToList()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller cancellation -- never wrap
        }
        catch (NamsOperationException ex) when (NamsFailureClassification.IsIdentitySecurityFailure(ex))
        {
            throw; // identity/security violations propagate unconditionally, matching Phases 3/4
        }
        catch (NamsOperationException ex) when (ex.FailureKind is NamsFailureKind.Network or NamsFailureKind.Timeout)
        {
            _logger.LogWarning(ex, "NAMS message persistence for conversation {ConversationId} timed out or lost network connectivity; write outcome is unknown.", namsConversationId);
            _metrics.UnknownWriteOutcomes.Add(1, NamsMetricTags.Operation("store_turn"));
            result = new NamsPersistenceResult
            {
                Outcome = NamsPersistenceOutcome.UnknownWriteOutcome,
                FailureReason = "NAMS message persistence timed out or lost network connectivity; whether the write succeeded is unknown -- not retried (no confirmed idempotency mechanism)."
            };
        }
        catch (NamsOperationException ex)
        {
            _logger.LogWarning(ex, "NAMS message persistence for conversation {ConversationId} was rejected.", namsConversationId);
            result = new NamsPersistenceResult
            {
                Outcome = NamsPersistenceOutcome.Failed,
                FailureReason = "NAMS message persistence failed."
            };
        }

        if (result.Outcome != NamsPersistenceOutcome.Persisted && _options.PersistenceFailureMode == NamsPersistenceFailureMode.FailInvocation)
            throw new NamsPersistenceFailedException(result);

        return result;
    }
}
