using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

return await LongMemEvalProgram.RunAsync(args);

internal static class LongMemEvalProgram
{
    private const int DefaultQuestions = 10;
    private const int DefaultSeed = 42;
    private const int DefaultMaxRelevant = 30;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--prepared-pair", StringComparer.Ordinal))
        {
            return await LongMemEvalPreparedPairProgram.RunAsync(args)
                .ConfigureAwait(false);
        }


        try
        {
            var options = Parse(args);
            ValidateInputs(options);
            if (options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
            {
                Console.Error.WriteLine(
                    "longmemeval: warning: content evidence retains public dataset questions, recalled text, and model answers; keep the output gitignored.");
            }

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var embeddingDeployment =
                RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
            var extractionDeployment =
                Environment.GetEnvironmentVariable("AZURE_OPENAI_EXTRACTION_DEPLOYMENT")
                ?? deployment;
            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));
            using var answerChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var diagnosticChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var extractionChatClient = options.MemoryMode.UsesExtraction()
                ? new LongMemEvalChatCallMeter(new ProviderCompatibleExtractionChatClient(
                    azureClient.GetChatClient(extractionDeployment).AsIChatClient()))
                : null;
            var embeddingGenerator = azureClient
                .GetEmbeddingClient(embeddingDeployment)
                .AsIEmbeddingGenerator();
            var embeddingDimensions = await LongMemEvalRuntime
                .ProbeEmbeddingDimensionsAsync(embeddingGenerator)
                .ConfigureAwait(false);

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath,
                options.Questions,
                options.Seed,
                options.JudgeRetryAttempts,
                options.EvidenceDetail,
                options.MaxRelevantMessages);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(
                options.DatasetPath, benchmarkOptions);

            var runId = $"longmemeval-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            await using var profile = await LongMemEvalMemoryProfile
                .StartAsync(
                    embeddingGenerator,
                    extractionChatClient,
                    options.MemoryMode,
                    extractionDeployment,
                    embeddingDimensions,
                    Console.Out,
                    CancellationToken.None)
                .ConfigureAwait(false);
            var adapter = new AgentMemoryLongMemEvalAdapter(
                profile.Services.GetRequiredService<IMemoryService>(),
                answerChatClient,
                runId,
                new LongMemEvalAdapterOptions
                {
                    MaxRelevantMessages = options.MaxRelevantMessages,
                    MemoryMode = options.MemoryMode,
                    MinSimilarityScore = 0,
                    ModelId = deployment,
                    EvidenceIndex = evidenceIndex,
                    EvidenceDetail = options.EvidenceDetail,
                    RequireGraphReadBack = options.MemoryMode.UsesExtraction(),
                    GraphProbe = options.MemoryMode.UsesExtraction()
                        ? new Neo4jLongMemEvalGraphProbe(
                            profile.Services.GetRequiredService<Neo4j.Driver.IDriver>())
                        : null,
                    ExtractionProgress = (completed, total) => Console.WriteLine(
                        $"longmemeval: extraction units {completed}/{total}.")
                });

            var runner = LongMemEvalBenchmarkRunner.Create(
                judgeChatClient, options.DatasetPath);
            var benchmarkConfig = new AgentBenchmarkConfig
            {
                AgentName = adapter.Name,
                ModelId = deployment,
                ReducerStrategy = $"AgentMemory {options.MemoryMode.ToString().ToLowerInvariant()} recall",
                MemoryProvider = "AgentMemory .NET / Neo4j 5.26"
            };

            Console.WriteLine(
                $"longmemeval: running {options.Questions} stratified questions, seed {options.Seed}, mode {options.MemoryMode.ToString().ToLowerInvariant()}, context cap {options.MaxRelevantMessages}.");
            var result = await runner
                .RunAsync(adapter, benchmarkConfig, benchmarkOptions)
                .ConfigureAwait(false);
            var postRunDiagnostics = await LongMemEvalPostRunDiagnostics.RunAsync(
                diagnosticChatClient,
                evidenceIndex,
                result.QuestionResults,
                adapter.QuestionTelemetry,
                options.OracleMode,
                options.JudgeRetryAttempts,
                retainContent: options.EvidenceDetail == LongMemEvalEvidenceDetail.Content)
                .ConfigureAwait(false);
            var answerCalls = answerChatClient.Snapshot();
            var judgeCalls = judgeChatClient.Snapshot();
            var diagnosticCalls = diagnosticChatClient.Snapshot();
            var extractionCalls = extractionChatClient?.Snapshot() ?? LongMemEvalChatCallSnapshot.Zero;
            var initialExtractionCalls =
                adapter.QuestionTelemetry.Sum(item => item.ExtractionUnits) * 4L;


            var validation = LongMemEvalRunValidator.Validate(
                options.Questions,
                result.TotalLlmCalls,
                adapter.QuestionTelemetry,
                result.QuestionResults,
                answerCalls,
                judgeCalls,
                extractionCalls,
                initialExtractionCalls);
            var destination = ResolveOutput(options.OutputPath, runId);
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
                    maxRelevantMessages = options.MaxRelevantMessages,
                    operatingMode = options.MemoryMode.Fingerprint(),
                    extractionModel = options.MemoryMode.UsesExtraction() ? extractionDeployment : null,
                    extractionTemperatureCompatibility = options.MemoryMode.UsesExtraction()
                        ? "explicit-zero-to-provider-default" : null,
                    extractionResponseFormat = options.MemoryMode.UsesExtraction()
                        ? "json-object" : null,
                    extractionSourceTime = options.MemoryMode.UsesExtraction()
                        ? "metadata-only-not-in-extraction-prompt" : null,
                    evidenceDetail = options.EvidenceDetail.ToString().ToLowerInvariant(),
                    oracleMode = options.OracleMode.ToString().ToLowerInvariant(),
                    judgeRetryAttempts = options.JudgeRetryAttempts,
                    embedding = new
                    {
                        provider = "Azure OpenAI",
                        deployment = embeddingDeployment,
                        dimensions = embeddingDimensions
                    },
                    judgeRequest = "AgentEval-source-native-null-temperature-256-tokens",
                    neo4jImage = "neo4j:5.26",
                    agentEval = typeof(ExternalBenchmarkOptions).Assembly.GetName().Version?.ToString(),
                    agentEvalDependency = "source-project:AgentEval.Memory"
                },
                agentMemory = new
                {
                    questions = adapter.QuestionTelemetry,
                    totalMessagesStored = adapter.QuestionTelemetry.Sum(item => item.MessagesStored),
                    totalItemsRetrieved = adapter.QuestionTelemetry.Sum(item => item.ItemsRetrieved),
                    totalExtractionUnits = adapter.QuestionTelemetry.Sum(item => item.ExtractionUnits),
                    totalRawMessagesRetrieved = adapter.QuestionTelemetry.Sum(item => item.RawMessagesRetrieved),
                    totalEntitiesRetrieved = adapter.QuestionTelemetry.Sum(item => item.EntitiesRetrieved),
                    totalFactsRetrieved = adapter.QuestionTelemetry.Sum(item => item.FactsRetrieved),
                    totalPreferencesRetrieved = adapter.QuestionTelemetry.Sum(item => item.PreferencesRetrieved),
                    graphRagQuestions = adapter.QuestionTelemetry.Count(item => item.GraphRagIncluded),
                    zeroStoreQuestions = adapter.QuestionTelemetry.Count(item => item.MessagesStored == 0),
                    zeroRecallQuestions = adapter.QuestionTelemetry.Count(item => item.ItemsRetrieved == 0)
                },
                callAccounting = new
                {
                    benchmarkLlmCalls = result.TotalLlmCalls,
                    diagnosticLlmCalls = postRunDiagnostics.DiagnosticLlmCalls,
                    totalLlmCalls = result.TotalLlmCalls + postRunDiagnostics.DiagnosticLlmCalls,
                    diagnosticCallsAffectScore = false,
                    observed = new
                    {
                        answer = Project(answerCalls),
                        judge = Project(judgeCalls),
                        extraction = Project(extractionCalls),
                        diagnostics = Project(diagnosticCalls)
                    },
                    extractionInitialExpectedCalls = initialExtractionCalls,
                    extractionRetryCalls = Math.Max(
                        0, extractionCalls.Calls - initialExtractionCalls)
                },
                postRunDiagnostics,
                result = validation.Accepted
                    ? LongMemEvalReportProjection.CreateAcceptedResult(
                        result, options.EvidenceDetail)
                    : null,
                diagnostic = validation.Accepted ? null : new
                {
                    result.BenchmarkId,
                    result.BenchmarkName,
                    result.Duration,
                    result.TotalLlmCalls,
                    questions = result.QuestionResults.Select((question, index) => new
                    {
                        question.QuestionId,
                        question.QuestionType,
                        question = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.Question
                            : null,
                        goldAnswer = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.GoldAnswer
                            : null,
                        agentResponse = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.AgentResponse
                            : null,
                        question.Correct,
                        question.RawScore,
                        judgeExplanation = options.EvidenceDetail == LongMemEvalEvidenceDetail.Content
                            ? question.JudgeExplanation
                            : null,
                        status = LongMemEvalRunValidator.Classify(
                            question,
                            adapter.QuestionTelemetry.FirstOrDefault(item =>
                                item.QuestionNumber == index + 1)),
                        question.Duration
                    })
                }
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

            Console.WriteLine(
                $"longmemeval: accuracy={result.OverallAccuracy:F1}% task_average={result.TaskAveragedAccuracy:F1}% questions={result.QuestionResults.Count} llm_calls={result.TotalLlmCalls}");
            Console.WriteLine($"longmemeval: report {destination}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: {exception.Message}");
            return 1;
        }
    }

    private static Options Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        return new Options(
            Value("--dataset") ?? string.Empty,
            ParsePositive(Value("--questions"), DefaultQuestions, "--questions"),
            ParsePositive(Value("--seed"), DefaultSeed, "--seed"),
            ParsePositive(Value("--max-relevant"), DefaultMaxRelevant, "--max-relevant"),
            ParseEvidenceDetail(Value("--evidence-detail")),
            ParseOracleMode(Value("--oracle")),
            ParseMemoryMode(Value("--memory-mode")),
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

    private static LongMemEvalEvidenceDetail ParseEvidenceDetail(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "identifiers" => LongMemEvalEvidenceDetail.Identifiers,
            "none" => LongMemEvalEvidenceDetail.None,
            "content" => LongMemEvalEvidenceDetail.Content,
            _ => throw new ArgumentException(
                "--evidence-detail must be one of: none, identifiers, content.")
        };

    private static LongMemEvalOracleMode ParseOracleMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "none" => LongMemEvalOracleMode.None,
            "failed" => LongMemEvalOracleMode.Failed,
            "all" => LongMemEvalOracleMode.All,
            _ => throw new ArgumentException("--oracle must be one of: none, failed, all.")
        };

    private static LongMemEvalMemoryMode ParseMemoryMode(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "raw" => LongMemEvalMemoryMode.Raw,
            "structured" => LongMemEvalMemoryMode.Structured,
            "hybrid" => LongMemEvalMemoryMode.Hybrid,
            _ => throw new ArgumentException(
                "--memory-mode must be one of: raw, structured, hybrid.")
        };

    private static int ParseNonNegative(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed < 0)
            throw new ArgumentException($"{option} must be a non-negative integer.");
        return parsed;
    }

    private static void ValidateInputs(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.DatasetPath))
            throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(options.DatasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", options.DatasetPath);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    private static string ResolveOutput(string? requested, string runId) =>
        Path.GetFullPath(requested ??
            Path.Combine("artifacts", "evaluation", runId, "report.json"));

    private static void PrintHelp() => Console.WriteLine(
        """
        AgentMemory LongMemEval (AgentEval.Memory local source)

        dotnet run --project tools/AgentMemory.LongMemEval -- \
          --dataset <longmemeval_s_cleaned.json> [--questions 10] [--seed 42] \
          [--max-relevant 30] [--memory-mode raw|structured|hybrid] \
          [--prepared-pair] \
          [--diagnostic-question N --diagnostic-source-session N] \
          [--evidence-detail none|identifiers|content] \
          [--oracle none|failed|all] [--judge-retries 2] [--output <report.json>]

        --prepared-pair prepares structured memory once, freezes it, clones it, and evaluates isolated Structured and Hybrid arms.
        Supplying both diagnostic selectors with --prepared-pair runs exactly one extraction unit and can never emit a report or execute recall/judging.


        Requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT,
        and AZURE_OPENAI_EMBEDDING_DEPLOYMENT.
        Uses real LongMemEval data, a pinned Neo4j 5.26 container, real Azure OpenAI embeddings,
        and the same Azure deployment for answers and AgentEval's type-specific judge.
        Structured/hybrid extraction may use AZURE_OPENAI_EXTRACTION_DEPLOYMENT; it defaults to the answer deployment.
        """);

    private sealed record Options(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        LongMemEvalEvidenceDetail EvidenceDetail,
        LongMemEvalOracleMode OracleMode,
        LongMemEvalMemoryMode MemoryMode,
        int JudgeRetryAttempts,
        string? OutputPath);
}
