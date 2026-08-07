using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G4-REF. The two reference arms that make every other LongMemEval number interpretable: a
/// no-memory floor and a full-history ceiling. Neither touches AgentMemory, so the guards here are
/// the arm's own exact contract — they are not, and must never become, a relaxation of the
/// AgentMemory validator.
/// </summary>
public sealed class LongMemEvalReferenceArmTests
{
    private const string Question = "What degree did I graduate with?";

    [Fact]
    public async Task NoMemoryArmSendsTheQuestionWithoutAnyHistory()
    {
        var client = new RecordingChatClient();
        var agent = CreateAgent(LongMemEvalReferenceArm.NoMemory, client);
        agent.InjectConversationHistory(History(3));

        _ = await agent.InvokeAsync(Question);

        var prompt = Assert.Single(client.UserPrompts);
        Assert.Contains(Question, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("user-turn", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("assistant-turn", prompt, StringComparison.Ordinal);
        var telemetry = Assert.Single(agent.QuestionTelemetry);
        Assert.Equal(0, telemetry.HistoryTurnsProvided);
        Assert.Equal("completed", telemetry.Status);
    }

    [Fact]
    public void NoMemoryArmDoesNotInstructTheModelThatMemoryWasRetrieved()
    {
        // The shipped prompt says "using only the retrieved memory below". With no memory block that
        // manufactures abstentions and understates the parametric floor, so the floor arm must not
        // inherit it.
        Assert.DoesNotContain(
            "retrieved memory",
            LongMemEvalReferenceArm.NoMemory.SystemPrompt(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FullHistoryArmSendsEveryRealTurnAndDropsFormatterBoilerplate()
    {
        var client = new RecordingChatClient();
        var agent = CreateAgent(LongMemEvalReferenceArm.FullHistory, client);
        agent.InjectConversationHistory(History(3));

        _ = await agent.InvokeAsync(Question);

        var prompt = Assert.Single(client.UserPrompts);
        Assert.Contains("user-turn-0", prompt, StringComparison.Ordinal);
        Assert.Contains("assistant-turn-2", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-BOUNDARY", prompt, StringComparison.Ordinal);

        var telemetry = Assert.Single(agent.QuestionTelemetry);
        // 3 turns => 6 injected messages, of which the stub marks 2 synthetic.
        Assert.Equal(4, telemetry.HistoryTurnsProvided);
        Assert.Equal(2, telemetry.SyntheticTurnsDropped);
        Assert.Equal(
            6,
            telemetry.HistoryTurnsProvided + telemetry.SyntheticTurnsDropped);
    }

    [Fact]
    public async Task FullHistoryArmRecordsAContextWindowRejectionAsASkipRatherThanFailingTheRun()
    {
        var client = new RecordingChatClient
        {
            Throw = new Azure.RequestFailedException(
                400,
                "This model's maximum context length is 128000 tokens.",
                "context_length_exceeded",
                innerException: null)
        };
        var agent = CreateAgent(LongMemEvalReferenceArm.FullHistory, client);
        agent.InjectConversationHistory(History(3));

        var response = await agent.InvokeAsync(Question);

        var telemetry = Assert.Single(agent.QuestionTelemetry);
        Assert.Equal("skipped-context-window", telemetry.Status);
        Assert.StartsWith("[REFERENCE-ARM-SKIPPED", response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyOtherProviderFailureStillFailsTheRun()
    {
        // A skip is only ever a context-window verdict. Everything else must stay fatal, or the arm
        // would quietly convert real outages into "the ceiling was not measurable".
        var client = new RecordingChatClient
        {
            Throw = new Azure.RequestFailedException(429, "Too Many Requests", "rate_limit", null)
        };
        var agent = CreateAgent(LongMemEvalReferenceArm.FullHistory, client);
        agent.InjectConversationHistory(History(3));

        await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => agent.InvokeAsync(Question));
    }

    [Fact]
    public void ArmsCarryDistinctFingerprintsThatCannotCollideWithAMemoryMode()
    {
        var fingerprints = new[]
        {
            LongMemEvalReferenceArm.NoMemory.Fingerprint(),
            LongMemEvalReferenceArm.FullHistory.Fingerprint(),
            LongMemEvalMemoryMode.Raw.Fingerprint(),
            LongMemEvalMemoryMode.Structured.Fingerprint(),
            LongMemEvalMemoryMode.Hybrid.Fingerprint()
        };

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith("reference-", LongMemEvalReferenceArm.NoMemory.Fingerprint(), StringComparison.Ordinal);
        Assert.StartsWith("reference-", LongMemEvalReferenceArm.FullHistory.Fingerprint(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorAcceptsAnExactArmAndReportsFittedAccuracySeparately()
    {
        var telemetry = new[]
        {
            Completed(1),
            Completed(2),
            Skipped(3)
        };

        var validation = LongMemEvalReferenceArmValidator.Validate(
            questionCount: 3,
            llmCalls: 6,
            telemetry: telemetry,
            questionResults: [Result("q1", true), Result("q2", false), Result("q3", false)],
            answerCalls: Snapshot(calls: 3, failures: 1),
            judgeCalls: Snapshot(calls: 3, failures: 0),
            diagnosticJudgeCalls: 0);

        Assert.True(validation.Accepted, string.Join(" | ", validation.Issues));
        Assert.Equal(1, validation.SkippedQuestions);
        // Fitted accuracy excludes the skip: 1 correct of 2 answerable, not 1 of 3.
        Assert.Equal(50d, validation.FittedAccuracyPercent);
    }

    [Fact]
    public void ValidatorRejectsAProviderFailureThatIsNotAnAccountedSkip()
    {
        var validation = LongMemEvalReferenceArmValidator.Validate(
            questionCount: 2,
            llmCalls: 4,
            telemetry: [Completed(1), Completed(2)],
            questionResults: [Result("q1", true), Result("q2", true)],
            answerCalls: Snapshot(calls: 2, failures: 1),
            judgeCalls: Snapshot(calls: 2, failures: 0),
            diagnosticJudgeCalls: 0);

        Assert.False(validation.Accepted);
        Assert.Contains(
            validation.Issues,
            issue => issue.Contains("failed answer calls", StringComparison.OrdinalIgnoreCase) &&
                     issue.Contains("context window", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidatorRejectsAnInexactAnswerCallCount()
    {
        var validation = LongMemEvalReferenceArmValidator.Validate(
            questionCount: 2,
            llmCalls: 4,
            telemetry: [Completed(1), Completed(2)],
            questionResults: [Result("q1", true), Result("q2", true)],
            answerCalls: Snapshot(calls: 1, failures: 0),
            judgeCalls: Snapshot(calls: 2, failures: 0),
            diagnosticJudgeCalls: 0);

        Assert.False(validation.Accepted);
    }

    [Fact]
    public void EveryQuestionSkippingIsANullResultNotAZeroScore()
    {
        var validation = LongMemEvalReferenceArmValidator.Validate(
            questionCount: 2,
            llmCalls: 4,
            telemetry: [Skipped(1), Skipped(2)],
            questionResults: [Result("q1", false), Result("q2", false)],
            answerCalls: Snapshot(calls: 2, failures: 2),
            judgeCalls: Snapshot(calls: 2, failures: 0),
            diagnosticJudgeCalls: 0);

        Assert.True(validation.Accepted, string.Join(" | ", validation.Issues));
        Assert.Equal(2, validation.SkippedQuestions);
        Assert.Null(validation.FittedAccuracyPercent);
    }

    private static LongMemEvalReferenceTelemetry Completed(int number) =>
        new(number, $"q{number}", "completed", 4, 2, 1_000, 250);

    private static LongMemEvalReferenceTelemetry Skipped(int number) =>
        new(number, $"q{number}", "skipped-context-window", 4, 2, 1_000, 250);

    private static QuestionResult Result(string id, bool correct) => new()
    {
        QuestionId = id,
        QuestionType = "single-session-user",
        Question = Question,
        GoldAnswer = "Business Administration",
        AgentResponse = correct ? "Business Administration" : "I do not know",
        Correct = correct,
        RawScore = correct ? 100 : 0,
        JudgeExplanation = correct ? "Judge said: yes" : "Judge said: no",
        Duration = TimeSpan.FromSeconds(1)
    };

    private static LongMemEvalChatCallSnapshot Snapshot(int calls, int failures) =>
        new(calls, failures, TimeSpan.Zero);

    private static LongMemEvalReferenceAgent CreateAgent(
        LongMemEvalReferenceArm arm,
        IChatClient client) =>
        new(client, arm, "reference-run", "test-model", new StubOriginResolver());

    private static IReadOnlyList<(string UserMessage, string AssistantResponse)> History(int turns) =>
        Enumerable.Range(0, turns)
            .Select(index => index == 1
                ? ($"SYNTHETIC-BOUNDARY-{index}", $"SYNTHETIC-BOUNDARY-{index}")
                : ($"user-turn-{index}", $"assistant-turn-{index}"))
            .ToArray();

    /// <summary>Marks the middle turn's two messages synthetic, mirroring formatter boilerplate.</summary>
    private sealed class StubOriginResolver : ILongMemEvalReferenceOriginResolver
    {
        public LongMemEvalReferenceOrigins Resolve(
            IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
            string prompt)
        {
            var flags = history
                .SelectMany(turn => new[] { turn.UserMessage, turn.AssistantResponse })
                .Select(content => content.StartsWith("SYNTHETIC", StringComparison.Ordinal))
                .ToArray();
            return new LongMemEvalReferenceOrigins("stub-question", flags);
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<string> _userPrompts = [];

        public Exception? Throw { get; init; }

        public IReadOnlyList<string> UserPrompts => _userPrompts;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _userPrompts.Add(string.Join(
                "\n",
                messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text)));
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "an answer")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
