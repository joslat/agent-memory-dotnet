using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// P1. How much accuracy does <b>noise in the assembled context</b> cost, holding retrieval recall and
/// the answering strategy constant?
/// </summary>
/// <remarks>
/// <para>
/// <b>The lead this tests.</b> The decomposed-answering experiment measured the monolithic oracle at
/// <b>27 of 29 (93%)</b> with gold-only context, while real runs score ~88% — and separately, 65 of 67
/// recorded wrong answers had gold already present. Those reconcile only one way: gold being
/// <i>present</i> is not the same as the context being <i>usable</i>. This sweep adds distractor
/// sessions to a context that already contains all the gold, so recall is pinned at 100% and the only
/// variable is how much wrong material sits beside the right answer.
/// </para>
/// <para>
/// <b>Distractors come from the question's own haystack</b>, never from elsewhere. A random session
/// from another conversation is trivially ignorable; the sessions the retriever actually competes
/// against are the ones in the same corpus, about the same person. Sampling anywhere else would
/// measure a strawman and report it as a precision result.
/// </para>
/// <para>
/// <b>The witness is the token count.</b> A sweep whose context does not grow with K measured nothing
/// — the distractors were never added — and would report a flat line as "noise does not matter". Each
/// K records its own mean context size, and identical sizes across K void the run.
/// </para>
/// </remarks>
internal static class LongMemEvalContextPrecisionProgram
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
            using var chatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath, options.Questions, options.Seed,
                judgeRetryAttempts: 0, LongMemEvalEvidenceDetail.Identifiers, maxRelevantMessages: 30);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);
            var questions = evidenceIndex.Questions.ToList();
            var judge = new LongMemEvalJudge(chatClient, NullLogger<LongMemEvalJudge>.Instance);

            Console.WriteLine(
                $"longmemeval: context-precision sweep over {questions.Count} questions, "
                + $"K = {string.Join(", ", options.DistractorCounts)}");

            var levels = new List<object>();
            var perQuestion = new List<object>();
            var voidReasons = new List<string>();

            foreach (var k in options.DistractorCounts)
            {
                var correct = 0;
                var comparable = 0;
                long totalChars = 0;
                var addedAny = 0;

                foreach (var question in questions)
                {
                    var context = BuildContext(question, k, options.Seed, out var distractorsAdded);
                    if (distractorsAdded > 0) addedAny++;
                    totalChars += context.Sum(entry => entry.Content.Length);

                    var prompt = AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                        context, question.InvocationPrompt, question.QuestionDate);

                    string answer;
                    try
                    {
                        var response = await chatClient.GetResponseAsync(
                        [
                            new ChatMessage(ChatRole.System, AgentMemoryLongMemEvalAdapter.SystemPrompt),
                            new ChatMessage(ChatRole.User, prompt),
                        ]).ConfigureAwait(false);
                        answer = response.Text ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        perQuestion.Add(new { k, question.QuestionId, status = $"threw:{ex.GetType().Name}" });
                        continue;
                    }

                    var judgment = await judge.JudgeAsync(
                        answer, ToBenchmarkQuestion(question)).ConfigureAwait(false);
                    var valid = LongMemEvalRunValidator.TryParseJudgeVerdict(
                        judgment.Explanation, out var parsed) && parsed == judgment.Correct;
                    if (!valid)
                    {
                        perQuestion.Add(new { k, question.QuestionId, status = "judge-invalid" });
                        continue;
                    }

                    comparable++;
                    if (judgment.Correct == true) correct++;
                    perQuestion.Add(new
                    {
                        k,
                        question.QuestionId,
                        question.QuestionType,
                        status = "completed",
                        correct = judgment.Correct,
                        distractorSessions = distractorsAdded,
                        contextChars = context.Sum(entry => entry.Content.Length),
                    });
                }

                // The witness. K > 0 that added nothing means the question had no non-gold sessions to
                // draw on, and a level built entirely from those is the gold-only level wearing a
                // different label.
                if (k > 0 && addedAny == 0)
                    voidReasons.Add($"K={k} added no distractors to any question");

                var accuracy = comparable == 0 ? (double?)null : (double)correct / comparable;
                levels.Add(new
                {
                    k,
                    comparable,
                    correct,
                    accuracy,
                    questionsWithDistractors = addedAny,
                    meanContextChars = questions.Count == 0 ? 0 : totalChars / questions.Count,
                });

                Console.WriteLine(
                    $"  K={k,-3} correct {correct}/{comparable}"
                    + (accuracy is { } a ? $" ({a:P1})" : " (n/a)")
                    + $"  meanContextChars={(questions.Count == 0 ? 0 : totalChars / questions.Count)}"
                    + $"  withDistractors={addedAny}/{questions.Count}");
            }

            // A sweep whose context never grows measured one condition several times.
            var sizes = levels.Select(level => (long)level.GetType().GetProperty("meanContextChars")!
                .GetValue(level)!).Distinct().Count();
            if (options.DistractorCounts.Count > 1 && sizes == 1)
                voidReasons.Add("mean context size identical at every K");

            var runId = $"context-precision-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            var report = new
            {
                schemaVersion = 1,
                runId,
                dataset = Path.GetFileName(options.DatasetPath),
                options.Questions,
                options.Seed,
                answerDeployment = deployment,
                distractorCounts = options.DistractorCounts,
                isVoid = voidReasons.Count > 0,
                voidReasons,
                levels,
                questions = perQuestion,
                calls = chatClient.Snapshot().Calls,
            };

            var output = options.OutputPath ?? Path.Combine("artifacts", "evaluation", $"{runId}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(
                output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"longmemeval: calls={chatClient.Snapshot().Calls}  report {output}");
            if (voidReasons.Count > 0)
            {
                Console.Error.WriteLine($"longmemeval: VOID — {string.Join("; ", voidReasons)}");
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

    /// <summary>
    /// Gold sessions plus <paramref name="distractorCount"/> non-gold sessions from the same question.
    /// </summary>
    /// <remarks>
    /// Message order is preserved across the whole selection rather than gold-first. Grouping the gold
    /// at the top would hand the model a positional cue no retriever provides, and the sweep would
    /// then measure how well it reads an ordered list.
    /// </remarks>
    internal static List<(string Role, string Timestamp, string Content)> BuildContext(
        LongMemEvalEvidenceQuestion question, int distractorCount, int seed, out int distractorsAdded)
    {
        ArgumentNullException.ThrowIfNull(question);

        var nonGold = question.Messages
            .Select(message => message.SourceSessionId)
            .Where(sessionId => !question.AnswerSessionIds.Contains(sessionId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(sessionId => sessionId, StringComparer.Ordinal)
            .ToList();

        // Deterministic given (question, seed): the same K must select the same sessions on a re-run,
        // or two levels of the sweep differ by their sample as well as by their size.
        var random = new Random(HashCode.Combine(seed, question.QuestionId.GetHashCode(StringComparison.Ordinal)));
        var chosen = nonGold.OrderBy(_ => random.Next()).Take(distractorCount)
            .ToHashSet(StringComparer.Ordinal);
        distractorsAdded = chosen.Count;

        return question.Messages
            .Where(message =>
                question.AnswerSessionIds.Contains(message.SourceSessionId) ||
                chosen.Contains(message.SourceSessionId))
            .Select(message => (message.Role, message.SourceTimestamp, message.FormattedContent))
            .ToList();
    }

    private static ExternalBenchmarkQuestion ToBenchmarkQuestion(LongMemEvalEvidenceQuestion indexed) => new()
    {
        QuestionId = indexed.QuestionId,
        QuestionType = indexed.QuestionType,
        Question = indexed.Question,
        GoldAnswer = indexed.GoldAnswer,
        QuestionDate = indexed.QuestionDate,
        IsAbstention = indexed.IsAbstention,
    };

    private static PrecisionOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }

        var datasetPath = Value("--dataset")
            ?? LongMemEvalDatasetLocator.Resolve(null, Environment.GetEnvironmentVariable)
            ?? throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
        if (!File.Exists(datasetPath))
            throw new FileNotFoundException("LongMemEval dataset not found.", datasetPath);

        var counts = (Value("--distractor-sessions") ?? "0,2,5,10")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException("--distractor-sessions must be non-negative integers."))
            .Distinct()
            .OrderBy(value => value)
            .ToList();

        return new PrecisionOptions(
            datasetPath,
            ParsePositive(Value("--questions"), 10, "--questions"),
            ParsePositive(Value("--seed"), 42, "--seed"),
            counts,
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

    private sealed record PrecisionOptions(
        string DatasetPath,
        int Questions,
        int Seed,
        IReadOnlyList<int> DistractorCounts,
        string? OutputPath);
}
