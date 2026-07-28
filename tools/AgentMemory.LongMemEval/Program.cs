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

        try
        {
            var options = Parse(args);
            ValidateInputs(options);

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var embeddingDeployment =
                RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));
            using var chatClient = LongMemEvalRuntime.CreateCompatibleChatClient(
                azureClient
                .GetChatClient(deployment)
                .AsIChatClient());
            var embeddingGenerator = azureClient
                .GetEmbeddingClient(embeddingDeployment)
                .AsIEmbeddingGenerator();
            var embeddingDimensions = await LongMemEvalRuntime
                .ProbeEmbeddingDimensionsAsync(embeddingGenerator)
                .ConfigureAwait(false);

            var runId = $"longmemeval-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            await using var profile = await LongMemEvalMemoryProfile
                .StartAsync(
                    embeddingGenerator, embeddingDimensions, Console.Out, CancellationToken.None)
                .ConfigureAwait(false);
            var adapter = new AgentMemoryLongMemEvalAdapter(
                profile.Services.GetRequiredService<IMemoryService>(),
                chatClient,
                runId,
                new LongMemEvalAdapterOptions
                {
                    MaxRelevantMessages = options.MaxRelevantMessages,
                    MinSimilarityScore = 0,
                    ModelId = deployment
                });

            var runner = LongMemEvalBenchmarkRunner.Create(chatClient, options.DatasetPath);
            var benchmarkConfig = new AgentBenchmarkConfig
            {
                AgentName = adapter.Name,
                ModelId = deployment,
                ReducerStrategy = "AgentMemory vector recall",
                MemoryProvider = "AgentMemory .NET / Neo4j 5.26"
            };
            var benchmarkOptions = new ExternalBenchmarkOptions
            {
                DatasetPath = options.DatasetPath,
                MaxQuestions = options.Questions,
                StratifiedSampling = true,
                RandomSeed = options.Seed,
                PreserveSessionBoundaries = true,
                IncludeTimestamps = true,
                HistoryInjectionMode = HistoryInjectionMode.StructuredChatHistory,
                DatasetMode = "S"
            };

            Console.WriteLine(
                $"longmemeval: running {options.Questions} stratified questions, seed {options.Seed}, retrieval cap {options.MaxRelevantMessages}.");
            var result = await runner
                .RunAsync(adapter, benchmarkConfig, benchmarkOptions)
                .ConfigureAwait(false);

            var validation = LongMemEvalRunValidator.Validate(
                options.Questions,
                result.TotalLlmCalls,
                adapter.QuestionTelemetry,
                result.QuestionResults);
            var destination = ResolveOutput(options.OutputPath, runId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var report = new
            {
                schemaVersion = 1,
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
                    embedding = new
                    {
                        provider = "Azure OpenAI",
                        deployment = embeddingDeployment,
                        dimensions = embeddingDimensions
                    },
                    judgeTemperatureCompatibility = "explicit-zero-to-provider-default",
                    neo4jImage = "neo4j:5.26",
                    agentEval = "0.16.0-beta"
                },
                agentMemory = new
                {
                    questions = adapter.QuestionTelemetry,
                    totalMessagesStored = adapter.QuestionTelemetry.Sum(item => item.MessagesStored),
                    totalItemsRetrieved = adapter.QuestionTelemetry.Sum(item => item.ItemsRetrieved),
                    zeroStoreQuestions = adapter.QuestionTelemetry.Count(item => item.MessagesStored == 0),
                    zeroRecallQuestions = adapter.QuestionTelemetry.Count(item => item.ItemsRetrieved == 0)
                },
                result = validation.Accepted ? result : null,
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
            Value("--output"));
    }

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
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
        AgentMemory LongMemEval (AgentEval 0.16.0-beta)

        dotnet run --project tools/AgentMemory.LongMemEval -- \
          --dataset <longmemeval_s_cleaned.json> [--questions 10] [--seed 42] \
          [--max-relevant 30] [--output <report.json>]

        Requires AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT,
        and AZURE_OPENAI_EMBEDDING_DEPLOYMENT.
        Uses real LongMemEval data, a pinned Neo4j 5.26 container, real Azure OpenAI embeddings,
        and the same Azure deployment for answers and AgentEval's type-specific judge.
        """);

    private sealed record Options(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxRelevantMessages,
        string? OutputPath);
}
