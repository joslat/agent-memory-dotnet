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

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            using var answerChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var diagnosticChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath,
                options.Questions,
                options.Seed,
                options.JudgeRetryAttempts,
                LongMemEvalEvidenceDetail.Identifiers,
                options.MaxRelevantMessages);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);

            var runId = $"longmemeval-reference-{options.Arm.ToString().ToLowerInvariant()}-" +
                $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var agent = new LongMemEvalReferenceAgent(
                answerChatClient,
                options.Arm,
                runId,
                deployment,
                new LongMemEvalEvidenceOriginResolver(evidenceIndex));

            Console.WriteLine(
                $"longmemeval: reference arm {options.Arm.Fingerprint()}, {options.Questions} stratified questions, seed {options.Seed}. " +
                "No Neo4j container, no embeddings, no extraction, no recall.");

            var runner = LongMemEvalBenchmarkRunner.Create(judgeChatClient, options.DatasetPath);
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
                options.JudgeRetryAttempts).ConfigureAwait(false);
            var diagnosticJudgeCalls = judgeRetries.Sum(retry => retry.LlmCalls);

            var answerCalls = answerChatClient.Snapshot();
            var judgeCalls = judgeChatClient.Snapshot();
            var telemetry = agent.QuestionTelemetry;
            var validation = LongMemEvalReferenceArmValidator.Validate(
                options.Questions,
                result.TotalLlmCalls,
                telemetry,
                result.QuestionResults,
                answerCalls,
                judgeCalls,
                judgeRetries.Count,
                options.JudgeRetryAttempts);

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
                    seed = options.Seed,
                    stratified = true,
                    answerModel = deployment,
                    judgeModel = deployment,
                    // Deliberately prefixed: a reference arm must never be comparable to an
                    // AgentMemory arm by accident in a ledger.
                    operatingMode = options.Arm.Fingerprint(),
                    memoryProvider = "none",
                    // Recorded verbatim because it necessarily differs from the shipped memory
                    // prompt, and that difference is a limitation of the comparison.
                    systemPrompt = options.Arm.SystemPrompt(),
                    contextFitDecidedBy = "provider-context-window-rejection-not-estimated",
                    judgeRequest = "AgentEval-source-native-null-temperature-256-tokens",
                    judgeRetryAttempts = options.JudgeRetryAttempts,
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
                  $"answered={validation.AnsweredQuestions}/{options.Questions} " +
                  $"skipped_context_window={validation.SkippedQuestions} " +
                  $"overall_including_skips={result.OverallAccuracy:F1}% llm_calls={result.TotalLlmCalls}"
                : $"longmemeval: arm={options.Arm.Fingerprint()} NOT MEASURABLE on this deployment — " +
                  $"all {validation.SkippedQuestions}/{options.Questions} questions exceeded the context window.");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: {exception.Message}");
            return 1;
        }
    }

    private static ReferenceOptions Parse(string[] args)
    {
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
            Value("--output"));
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

    private sealed record ReferenceOptions(
        LongMemEvalReferenceArm Arm,
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        int JudgeRetryAttempts,
        string? OutputPath);
}
