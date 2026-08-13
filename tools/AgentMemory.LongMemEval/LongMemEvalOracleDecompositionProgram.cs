using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// B4. Answers the same questions twice from the <b>same</b> perfect context — once monolithically,
/// once decomposed — so the only variable is decomposition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this verb exists, and why it needs no infrastructure.</b> Across 62 recorded reports, 65 of
/// 67 wrong answers had the gold evidence already retrieved or present. The loss is at the answering
/// stage, where no retrieval change can reach it. The oracle reads gold sessions straight from the
/// dataset, so this comparison needs <b>no Neo4j, no Docker, no prepared corpus and no extraction</b>
/// — only answer and judge calls.
/// </para>
/// <para>
/// <b>It is an upper bound and is designed to kill, not to endorse.</b> Perfect context is the most
/// favourable condition decomposition will ever see. If it cannot win here it cannot win on real
/// retrieval, so a null result ends the architecture rather than inviting a bigger run.
/// </para>
/// </remarks>
internal static class LongMemEvalOracleDecompositionProgram
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

            // Metered separately per arm. A single meter would report a total that cannot be checked
            // against either arm's expected count, and the expected counts differ by construction
            // (monolithic 2; decomposed subQuestions + 3).
            using var monolithicClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());
            using var decomposedClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath,
                options.Questions,
                options.Seed,
                judgeRetryAttempts: 0,
                LongMemEvalEvidenceDetail.Identifiers,
                maxRelevantMessages: 30);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);

            var selected = Select(evidenceIndex.Questions, options.QuestionIds);
            if (selected.Count == 0)
            {
                Console.Error.WriteLine(
                    "longmemeval: no questions selected. --question-ids matched nothing in the sample; "
                    + "widen --questions or check the ids.");
                return 2;
            }

            // Both arms share ONE judge instance and one deployment: a verdict difference caused by a
            // differently-configured judge would be indistinguishable from a difference caused by
            // decomposition, which is the whole quantity being measured.
            var judge = new LongMemEvalJudge(monolithicClient, NullLogger<LongMemEvalJudge>.Instance);
            var decomposedJudge = new LongMemEvalJudge(decomposedClient, NullLogger<LongMemEvalJudge>.Instance);

            Console.WriteLine(
                $"longmemeval: oracle decomposition over {selected.Count} questions "
                + $"(max {options.MaxSubQuestions} sub-questions).");

            var pairs = new List<LongMemEvalOraclePair>();
            var details = new List<object>();
            var monoCalls = 0;
            var decCalls = 0;

            foreach (var (question, index) in selected.Select((q, i) => (q, i)))
            {
                // Sequential on purpose. Cost must stay predictable and interruptible: a partial run
                // whose completed pairs are already written is worth more than a fast run that has to
                // be discarded, and this arm's call count is variable per question.
                var monolithic = await LongMemEvalPostRunDiagnostics.RunOracleAsync(
                        monolithicClient, judge, question, options.RetainContent, CancellationToken.None)
                    .ConfigureAwait(false);
                monoCalls += monolithic.LlmCalls;

                var decomposed = await LongMemEvalDecomposedOracle.RunAsync(
                        decomposedClient, decomposedJudge, question, options.MaxSubQuestions,
                        options.RetainContent, CancellationToken.None)
                    .ConfigureAwait(false);
                decCalls += decomposed.LlmCalls;

                pairs.Add(new LongMemEvalOraclePair(
                    question.QuestionId,
                    monolithic.ValidVerdict ? monolithic.Correct : null,
                    decomposed.ValidVerdict ? decomposed.Correct : null,
                    decomposed.SubQuestions.Count));

                details.Add(new
                {
                    question.QuestionId,
                    question.QuestionType,
                    question.IsAbstention,
                    monolithic = new
                    {
                        monolithic.Status,
                        monolithic.Correct,
                        monolithic.ValidVerdict,
                        monolithic.LlmCalls,
                        answer = options.RetainContent ? monolithic.Answer : null,
                    },
                    decomposed = new
                    {
                        decomposed.Status,
                        decomposed.Correct,
                        decomposed.ValidVerdict,
                        decomposed.LlmCalls,
                        expectedCalls = LongMemEvalDecomposedOracle.ExpectedCalls(decomposed.SubQuestions.Count),
                        // Recorded verbatim so the decomposition is auditable and the downstream half
                        // is re-runnable without re-deciding how the question was split.
                        subQuestions = decomposed.SubQuestions,
                        subAnswers = options.RetainContent ? decomposed.SubAnswers : null,
                        composedAnswer = options.RetainContent ? decomposed.ComposedAnswer : null,
                    },
                });

                Console.WriteLine(
                    $"  [{index + 1}/{selected.Count}] {question.QuestionId} "
                    + $"mono={Verdict(monolithic.ValidVerdict, monolithic.Correct)} "
                    + $"dec={Verdict(decomposed.ValidVerdict, decomposed.Correct)} "
                    + $"split={decomposed.SubQuestions.Count}");
            }

            var comparison = LongMemEvalOracleComparison.From(pairs);
            var runId = $"oracle-decomposition-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var report = new
            {
                schemaVersion = 1,
                runId,
                dataset = Path.GetFileName(options.DatasetPath),
                options.Questions,
                options.Seed,
                options.MaxSubQuestions,
                answerDeployment = deployment,
                questionsRun = selected.Count,
                comparison = new
                {
                    comparison.Comparable,
                    comparison.BothCorrect,
                    comparison.BothWrong,
                    comparison.DecomposedOnly,
                    comparison.MonolithicOnly,
                    comparison.Inconclusive,
                    comparison.ActuallyDecomposed,
                    comparison.IsVoid,
                    summary = comparison.Describe(),
                },
                calls = new
                {
                    monolithic = monoCalls,
                    decomposed = decCalls,
                    total = monoCalls + decCalls,
                },
                questions = details,
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var output = options.OutputPath ?? Path.Combine("artifacts", "evaluation", $"{runId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"longmemeval: {comparison.Describe()}");
            Console.WriteLine($"longmemeval: calls monolithic={monoCalls} decomposed={decCalls}");
            Console.WriteLine($"longmemeval: report {output}");

            if (comparison.IsVoid)
            {
                // Non-zero, deliberately. A void run that exits 0 gets read as "no difference found",
                // which is the exact misreading the witness exists to prevent.
                Console.Error.WriteLine(
                    "longmemeval: VOID — this run cannot support a conclusion about decomposition.");
                return 3;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"longmemeval: {ex.Message}");
            return 1;
        }
    }

    private static string Verdict(bool valid, bool? correct) =>
        !valid ? "?" : correct == true ? "Y" : "n";

    private static IReadOnlyList<LongMemEvalEvidenceQuestion> Select(
        IEnumerable<LongMemEvalEvidenceQuestion> questions, IReadOnlySet<string> ids)
    {
        var all = questions.ToList();
        return ids.Count == 0
            ? all
            : all.Where(question => ids.Contains(question.QuestionId)).ToList();
    }

    private static DecompositionOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        var datasetPath = Value("--dataset")
            ?? LongMemEvalDatasetLocator.Resolve(null, Environment.GetEnvironmentVariable)
            ?? throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(datasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", datasetPath);

        var ids = (Value("--question-ids") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return new DecompositionOptions(
            datasetPath,
            ParsePositive(Value("--questions"), 10, "--questions"),
            ParsePositive(Value("--seed"), 42, "--seed"),
            ParsePositive(Value("--max-sub-questions"), 4, "--max-sub-questions"),
            ids,
            // Content is retained by default here, unlike a scored run: the sub-questions and
            // sub-answers ARE the finding, and a comparison whose decompositions cannot be read
            // afterwards cannot be argued with.
            !args.Contains("--no-content", StringComparer.Ordinal),
            Value("--output"));
    }

    private static int ParsePositive(string? value, int defaultValue, string option)
    {
        if (value is null) return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer.");
        return parsed;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{name} is required; refusing to create a synthetic LongMemEval score.");

    private sealed record DecompositionOptions(
        string DatasetPath,
        int Questions,
        int Seed,
        int MaxSubQuestions,
        IReadOnlySet<string> QuestionIds,
        bool RetainContent,
        string? OutputPath);
}
