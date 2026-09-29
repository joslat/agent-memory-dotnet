using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// G4-REF. Runs a no-memory floor or a full-history ceiling on the identical sample, seed, answer
/// deployment and judge as the AgentMemory arms, so the three numbers bracket each other. Dispatched
/// separately from <see cref="LongMemEvalProgram"/> so the accepted AgentMemory path is untouched.
/// </summary>
internal static class LongMemEvalReferenceArmProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);

            // --question-ids narrows the usual draw, exactly as it does for the prepared pair, and before
            // any client exists: an id outside the draw stops the run before a single call is made.
            var selection = LongMemEvalQuestionSelection.Resolve(
                options.DatasetPath,
                CreateBenchmarkOptions(options),
                options.QuestionFilter,
                (path, count) => CreateBenchmarkOptions(options with
                {
                    DatasetPath = path,
                    Questions = count,
                    // The derived file holds exactly the kept questions, so it is read as it stands.
                    AbstentionPolicy = AbstentionSamplingPolicy.AsSampled,
                    AbstentionProportion = null,
                }));
            var questionCount = options.QuestionFilter is null ? options.Questions : selection.QuestionCount;
            if (selection.Record is { } filterRecord)
            {
                Console.WriteLine(
                    $"longmemeval: {LongMemEvalQuestionFilter.Option} keeps {filterRecord.QuestionCount} of "
                    + $"{filterRecord.DrawnQuestions} drawn questions ({filterRecord.Draw}): "
                    + $"{string.Join(",", filterRecord.QuestionIds)}.");
            }

            var model = HarnessClients.Create();
            // THE RUN IDENTITY, not a deployment name. PR #224 stamped the model and the
            // backend build but not the HOST; the same model id on two providers is not the
            // same measurement, and without this the two artifacts are indistinguishable.
            var deployment = model.AnswerIdentity;
            using var answerChatClient = new LongMemEvalChatCallMeter(
                model.CreateAnswerClient());
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                model.CreateJudgeClient());
            using var diagnosticChatClient = new LongMemEvalChatCallMeter(
                model.CreateAnswerClient());

            var benchmarkOptions = selection.Options;
            var evidenceIndex = selection.EvidenceIndex
                ?? LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);

            var runId = $"longmemeval-reference-{options.Arm.ToString().ToLowerInvariant()}-" +
                $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var agent = new LongMemEvalReferenceAgent(
                answerChatClient,
                options.Arm,
                runId,
                deployment,
                new LongMemEvalEvidenceOriginResolver(evidenceIndex));

            Console.WriteLine(
                $"longmemeval: reference arm {options.Arm.Fingerprint()}, {questionCount} stratified questions, seed {options.Seed}. " +
                "No Neo4j container, no embeddings, no extraction, no recall.");

            var runner = LongMemEvalBenchmarkRunner.Create(judgeChatClient, selection.DatasetPath);
            var result = await runner.RunAsync(
                agent,
                new AgentBenchmarkConfig
                {
                    AgentName = agent.Name,
                    ModelId = deployment,
                    ReducerStrategy = options.Arm.Fingerprint(),
                    MemoryProvider = "none (reference arm)"
                },
                benchmarkOptions).ConfigureAwait(false);

            var judgeRetries = await LongMemEvalPostRunDiagnostics.RetryInvalidJudgeVerdictsAsync(
                diagnosticChatClient,
                evidenceIndex,
                result.QuestionResults,
                options.JudgeRetryAttempts,
                verdictProtocol: options.JudgeProtocol).ConfigureAwait(false);
            var diagnosticJudgeCalls = judgeRetries.Sum(retry => retry.LlmCalls);

            var answerCalls = answerChatClient.Snapshot();
            var judgeCalls = judgeChatClient.Snapshot();
            var telemetry = agent.QuestionTelemetry;
            var validation = LongMemEvalReferenceArmValidator.Validate(
                questionCount,
                result.TotalLlmCalls,
                telemetry,
                result.QuestionResults,
                answerCalls,
                judgeCalls,
                judgeRetries.Count,
                options.JudgeRetryAttempts,
                options.JudgeProtocol);

            var destination = Path.GetFullPath(options.OutputPath ??
                Path.Combine("artifacts", "evaluation", runId, "report.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var report = new
            {
                schemaVersion = 2,
                runId,
                generatedAtUtc = DateTimeOffset.UtcNow,
                accepted = validation.Accepted,
                validationIssues = validation.Issues,
                fingerprint = new
                {
                    dataset = Path.GetFileName(options.DatasetPath),
                    datasetSha256 = Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(
                            await File.ReadAllBytesAsync(options.DatasetPath).ConfigureAwait(false))),
                    questions = options.Questions,
                    // The questions actually evaluated: the draw above, or the --question-ids subset of it.
                    questionsEvaluated = questionCount,
                    questionFilter = selection.Record,
                    seed = options.Seed,
                    stratified = true,
                    answerModel = deployment,
                    // The JUDGE's identity. This read `deployment` (the answer model) at all three report
                    // sites, which was true only while one deployment served both roles; with AI_JUDGE_*
                    // set, every report claimed the subject had graded itself.
                    judgeModel = model.JudgeIdentity,
                    // Deliberately prefixed: a reference arm must never be comparable to an
                    // AgentMemory arm by accident in a ledger.
                    operatingMode = options.Arm.Fingerprint(),
                    memoryProvider = "none",
                    // Recorded verbatim because it necessarily differs from the shipped memory
                    // prompt, and that difference is a limitation of the comparison.
                    systemPrompt = options.Arm.SystemPrompt(),
                    contextFitDecidedBy = "provider-context-window-rejection-not-estimated",
                    judgeRequest = $"AgentEval-source-native-null-temperature-{options.JudgeMaxOutputTokens}-tokens",
                    judgeRetryAttempts = options.JudgeRetryAttempts,
                    // The sampling that decided WHICH questions ran. Before these were forwarded, a
                    // reference arm silently drew the as-sampled set whatever it was asked for, so an
                    // artifact that does not state them cannot be matched to the arm it brackets.
                    abstentionPolicy = options.AbstentionPolicy.ToString(),
                    abstentionProportion = options.AbstentionProportion,
                    judgeVerdictProtocol = options.JudgeProtocol.ToString(),
                    agentEval = typeof(ExternalBenchmarkOptions).Assembly.GetName().Version?.ToString(),
                    agentEvalDependency = "source-project:AgentEval.Memory"
                },
                referenceArm = new
                {
                    arm = options.Arm.ToString(),
                    questions = telemetry,
                    skippedQuestions = validation.SkippedQuestions,
                    answeredQuestions = validation.AnsweredQuestions,
                    correctQuestions = validation.CorrectQuestions,
                    // The headline. Overall accuracy counts a skip as wrong; fitted accuracy
                    // excludes it, because "did not fit" is not "answered incorrectly".
                    fittedAccuracyPercent = validation.FittedAccuracyPercent,
                    totalHistoryMessagesProvided = telemetry.Sum(item => item.HistoryMessagesProvided),
                    totalSyntheticMessagesDropped = telemetry.Sum(item => item.SyntheticMessagesDropped)
                },
                callAccounting = new
                {
                    benchmarkLlmCalls = result.TotalLlmCalls,
                    diagnosticLlmCalls = diagnosticJudgeCalls,
                    totalLlmCalls = result.TotalLlmCalls + diagnosticJudgeCalls,
                    diagnosticCallsAffectScore = false,
                    observed = new
                    {
                        answer = Project(answerCalls),
                        judge = Project(judgeCalls),
                        diagnostics = Project(diagnosticChatClient.Snapshot())
                    }
                },
                judgeRetries,
                result = validation.Accepted
                    ? LongMemEvalReportProjection.CreateAcceptedResult(
                        result, LongMemEvalEvidenceDetail.Identifiers)
                    : null
            };
            await File.WriteAllTextAsync(
                destination,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) +
                Environment.NewLine).ConfigureAwait(false);

            if (!validation.Accepted)
            {
                foreach (var issue in validation.Issues)
                    Console.Error.WriteLine($"longmemeval: validation: {issue}");
                Console.Error.WriteLine($"longmemeval: rejected diagnostic report {destination}");
                return 1;
            }

            // A null fitted accuracy is a real outcome, not a zero: it means the history did not fit
            // this deployment for any question, so the ceiling is simply not measurable here.
            Console.WriteLine(validation.FittedAccuracyPercent is { } fitted
                ? $"longmemeval: arm={options.Arm.Fingerprint()} fitted_accuracy={fitted:F1}% " +
                  $"answered={validation.AnsweredQuestions}/{questionCount} " +
                  $"skipped_context_window={validation.SkippedQuestions} " +
                  $"overall_including_skips={result.OverallAccuracy:F1}% llm_calls={result.TotalLlmCalls}"
                : $"longmemeval: arm={options.Arm.Fingerprint()} NOT MEASURABLE on this deployment — " +
                  $"all {validation.SkippedQuestions}/{questionCount} questions exceeded the context window.");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Every option this verb accepts. Kept beside the parser so the two move together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This verb used to accept anything.</b> <c>Program</c> dispatches <c>--reference-arm</c>
    /// before its own validator runs, and this parser only ever read the names it knew, so a flag it did
    /// not know vanished. That made a set-B command run set A: the sampling flags were dropped, the arm
    /// drew a different 50 questions, and the report looked like a normal bracket of the prepared pair.
    /// </para>
    /// <para>
    /// The incompatible memory-arm flags are listed deliberately, so they reach their own, more specific
    /// refusal below instead of a generic "unknown option".
    /// </para>
    /// </remarks>
    internal static readonly string[] KnownOptions =
    [
        "--reference-arm", "--dataset", "--questions", "--seed", "--max-relevant", "--judge-retries",
        "--output", "--abstention", "--abstention-proportion", "--judge-protocol", "--question-ids",
        "--judge-max-output-tokens",
        // Recognised only to be refused with a specific reason.
        "--prepared-pair", "--memory-mode", "--exclude-synthetic-messages", "--oracle",
    ];

    /// <summary>
    /// The AgentEval options this arm runs under, built the same way the prepared pair builds its own.
    /// </summary>
    /// <remarks>
    /// A separate method so the forwarding is testable without a model: the sampling flags have to reach
    /// <c>ExternalBenchmarkOptions</c>, not merely parse.
    /// </remarks>
    internal static ExternalBenchmarkOptions CreateBenchmarkOptions(ReferenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return LongMemEvalBenchmarkProtocol.CreateOptions(
            options.DatasetPath,
            options.Questions,
            options.Seed,
            options.JudgeRetryAttempts,
            LongMemEvalEvidenceDetail.Identifiers,
            options.MaxRelevantMessages,
            options.JudgeProtocol,
            abstentionPolicy: options.AbstentionPolicy,
            abstentionTargetProportion: options.AbstentionProportion,
            judgeMaxOutputTokens: options.JudgeMaxOutputTokens);
    }

    internal static ReferenceOptions Parse(string[] args)
    {
        LongMemEvalArgumentValidator.Validate(args, KnownOptions);

        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        // Fail closed on a flag that has no defined meaning for an arm with no memory, rather than
        // accepting it and silently doing something else.
        foreach (var incompatible in new[] { "--prepared-pair", "--memory-mode", "--exclude-synthetic-messages" })
        {
            if (Array.IndexOf(args, incompatible) >= 0)
            {
                throw new ArgumentException(
                    $"{incompatible} cannot be combined with --reference-arm: a reference arm uses no AgentMemory.");
            }
        }

        if (Value("--oracle") is { } oracle &&
            !string.Equals(oracle, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--oracle is a memory-arm diagnostic and cannot be combined with --reference-arm.");
        }

        var arm = Value("--reference-arm")?.ToLowerInvariant() switch
        {
            "no-memory" => LongMemEvalReferenceArm.NoMemory,
            "full-history" => LongMemEvalReferenceArm.FullHistory,
            _ => throw new ArgumentException("--reference-arm must be one of: no-memory, full-history.")
        };

        var datasetPath = Value("--dataset") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(datasetPath))
            throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(datasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", datasetPath);

        return new ReferenceOptions(
            arm,
            datasetPath,
            ParsePositive(Value("--questions"), 10, "--questions"),
            ParsePositive(Value("--seed"), 42, "--seed"),
            ParsePositive(Value("--max-relevant"), 30, "--max-relevant"),
            ParseNonNegative(Value("--judge-retries"), 2, "--judge-retries"),
            Value("--output"),
            LongMemEvalSamplingOptions.ParseAbstention(Value("--abstention")),
            LongMemEvalSamplingOptions.ParseAbstentionProportion(Value("--abstention-proportion")),
            LongMemEvalSamplingOptions.ParseJudgeProtocol(Value("--judge-protocol")),
            LongMemEvalQuestionFilter.Parse(Value("--question-ids")),
            LongMemEvalSamplingOptions.ParseJudgeMaxOutputTokens(Value("--judge-max-output-tokens")));
    }

    private static object Project(LongMemEvalChatCallSnapshot snapshot) => new
    {
        snapshot.Calls,
        snapshot.Failures,
        durationMs = snapshot.Duration.TotalMilliseconds
    };

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static int ParseNonNegative(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return parsed;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    internal sealed record ReferenceOptions(
        LongMemEvalReferenceArm Arm,
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        int JudgeRetryAttempts,
        string? OutputPath,
        AbstentionSamplingPolicy AbstentionPolicy = AbstentionSamplingPolicy.AsSampled,
        double? AbstentionProportion = null,
        JudgeVerdictProtocol JudgeProtocol = JudgeVerdictProtocol.FreeText,
        LongMemEvalQuestionFilter? QuestionFilter = null,
        int JudgeMaxOutputTokens = LongMemEvalBenchmarkProtocol.DefaultJudgeMaxOutputTokens);
}
