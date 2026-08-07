using System.Collections.ObjectModel;
using AgentEval.Core;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// G4-REF. Drives a LongMemEval question through the answer model with either no context at all
/// (the floor) or the entire de-contaminated conversation (the ceiling). AgentMemory is never
/// constructed, so there is no container, no embedding, no extraction, and no recall.
/// </summary>
internal sealed class LongMemEvalReferenceAgent(
    IChatClient chatClient,
    LongMemEvalReferenceArm arm,
    string runId,
    string? modelId,
    ILongMemEvalReferenceOriginResolver originResolver)
    : IEvaluableAgent, IHistoryInjectableAgent, ISessionResettableAgent
{
    /// <summary>
    /// Returned instead of an answer when the provider rejects the prompt for exceeding its context
    /// window. It is a recorded outcome, not an error: the judge still runs, and the arm's validator
    /// excludes the question from fitted accuracy rather than scoring it wrong.
    /// </summary>
    internal const string SkippedAnswer =
        "[REFERENCE-ARM-SKIPPED: the conversation history exceeds this deployment's context window]";

    private readonly object _stateLock = new();
    private readonly List<LongMemEvalReferenceTelemetry> _telemetry = [];
    private IReadOnlyList<(string UserMessage, string AssistantResponse)>? _pendingHistory;
    private int _questionNumber;

    public string Name => $"AgentMemory.LongMemEval.Reference.{arm}";

    public IReadOnlyList<LongMemEvalReferenceTelemetry> QuestionTelemetry
    {
        get
        {
            lock (_stateLock)
                return new ReadOnlyCollection<LongMemEvalReferenceTelemetry>(_telemetry.ToArray());
        }
    }

    public void InjectConversationHistory(
        IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(conversationTurns);
        var materialized = conversationTurns.ToArray();
        lock (_stateLock)
        {
            if (_pendingHistory is not null)
            {
                throw new InvalidOperationException(
                    "LongMemEval history was injected more than once for the same question.");
            }

            _pendingHistory = materialized;
        }
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            _questionNumber++;
            _pendingHistory = null;
        }

        return Task.CompletedTask;
    }

    public async Task<AgentResponse> InvokeAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        IReadOnlyList<(string UserMessage, string AssistantResponse)> history;
        int questionNumber;
        lock (_stateLock)
        {
            history = _pendingHistory
                ?? throw new InvalidOperationException(
                    "LongMemEval question cannot run before conversation history is injected.");
            if (history.Count == 0)
            {
                throw new InvalidOperationException(
                    "LongMemEval question cannot run with empty conversation history.");
            }

            _pendingHistory = null;
            questionNumber = _questionNumber;
        }

        // Resolved for both arms: the floor does not use the turns, but resolving keeps the evidence
        // index consumed in lockstep with the runner and proves the same question set was sampled.
        var origins = originResolver.Resolve(history, prompt);

        var turns = new List<(string Role, string Content)>();
        var dropped = 0;
        if (arm.UsesHistory())
        {
            var ordinal = 0;
            foreach (var (user, assistant) in history)
            {
                Add("user", user);
                Add("assistant", assistant);

                void Add(string role, string content)
                {
                    var current = ordinal++;
                    if (current < origins.IsSynthetic.Count && origins.IsSynthetic[current])
                    {
                        dropped++;
                        return;
                    }

                    turns.Add((role, content));
                }
            }
        }

        var answerPrompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(turns, prompt);

        ChatResponse response;
        try
        {
            response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, arm.SystemPrompt()),
                    new ChatMessage(ChatRole.User, answerPrompt)
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested && IsContextWindowRejection(exception))
        {
            // The provider is the authority on whether the history fits. Recording its verdict is
            // exactly the measurement this arm exists to take, so it is not a failure.
            Record(questionNumber, origins.QuestionId, "skipped-context-window", turns.Count, dropped, answerPrompt);
            return new AgentResponse { Text = SkippedAnswer, ModelId = modelId };
        }

        Record(questionNumber, origins.QuestionId, "completed", turns.Count, dropped, answerPrompt);
        return new AgentResponse
        {
            Text = response.Text ?? string.Empty,
            ModelId = modelId,
            AdditionalProperties = new Dictionary<string, object?>
            {
                ["referenceArm"] = arm.Fingerprint(),
                ["referenceArm.runId"] = runId,
                ["referenceArm.historyTurnsProvided"] = turns.Count
            }
        };
    }

    /// <summary>
    /// Narrow on purpose. Only a context-length verdict may become a skip; a rate limit, an outage,
    /// or an auth failure must stay fatal, or the arm would quietly report real breakage as
    /// "the ceiling was not measurable".
    /// </summary>
    internal static bool IsContextWindowRejection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Azure.RequestFailedException { Status: 400 } failed &&
                (string.Equals(failed.ErrorCode, "context_length_exceeded", StringComparison.Ordinal) ||
                 failed.Message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
                 failed.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private void Record(
        int questionNumber,
        string questionId,
        string status,
        int turnsProvided,
        int dropped,
        string answerPrompt)
    {
        lock (_stateLock)
        {
            _telemetry.Add(new LongMemEvalReferenceTelemetry(
                questionNumber,
                questionId,
                status,
                turnsProvided,
                dropped,
                answerPrompt.Length,
                // Labelled an estimate and reported only. It is never used to decide whether the
                // prompt fits — the provider decides that, because at 113k-128k every question in
                // this dataset sits inside the estimator's own error bar.
                (int)Math.Ceiling(answerPrompt.Length / 4.0)));
        }
    }
}
