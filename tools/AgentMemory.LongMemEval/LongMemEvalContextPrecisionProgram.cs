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
                + $"levels = {string.Join(", ", options.Levels.Select(l => $"(K={l.Distractors},gold={l.GoldFraction:0.##})"))}");

            var levels = new List<object>();
            var perQuestion = new List<object>();
            var voidReasons = new List<string>();

            foreach (var (k, goldFraction) in options.Levels)
            {
                var correct = 0;
                var comparable = 0;
                long totalChars = 0;
                var addedAny = 0;
                var goldDropped = 0;

                foreach (var question in questions)
                {
                    var goldKept = 0;
                    var goldTotal = 0;
                    var context = BuildContext(
                        question, k, options.Seed, out var distractorsAdded, goldFraction,
                        (kept, total) =>
                        {
                            goldKept = kept;
                            goldTotal = total;
                            if (kept < total) goldDropped++;
                        });
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
                        goldFraction,
                        question.QuestionId,
                        question.QuestionType,
                        status = "completed",
                        correct = judgment.Correct,
                        distractorSessions = distractorsAdded,
                        // REALISED coverage, not the nominal fraction. keepCount is a ceiling over a
                        // per-question session count, so one nominal level produces many different
                        // actual coverages -- a question with 2 gold sessions is untouched at 0.75 and
                        // halved at 0.5, while one with 8 steps through 6, 5, 4. Recording the real
                        // ratio lets every level pool into one curve, which is a far finer measurement
                        // than the four nominal points cost.
                        goldSessionsKept = goldKept,
                        goldSessionsTotal = goldTotal,
                        goldCoverage = goldTotal == 0 ? (double?)null : (double)goldKept / goldTotal,
                        contextChars = context.Sum(entry => entry.Content.Length),
                    });
                }

                // The witness. K > 0 that added nothing means the question had no non-gold sessions to
                // draw on, and a level built entirely from those is the gold-only level wearing a
                // different label.
                if (k > 0 && addedAny == 0)
                    voidReasons.Add($"K={k} added no distractors to any question");
                // The completeness witness. A fraction below 1 that dropped nothing is the full-gold
                // level wearing a different label, and would report "completeness does not matter".
                if (goldFraction < 1.0 && goldDropped == 0)
                    voidReasons.Add($"goldFraction={goldFraction} dropped no gold from any question");

                var accuracy = comparable == 0 ? (double?)null : (double)correct / comparable;
                levels.Add(new
                {
                    k,
                    goldFraction,
                    goldDropped,
                    comparable,
                    correct,
                    accuracy,
                    questionsWithDistractors = addedAny,
                    meanContextChars = questions.Count == 0 ? 0 : totalChars / questions.Count,
                });

                Console.WriteLine(
                    $"  K={k,-3} gold={goldFraction,-5:0.##} correct {correct}/{comparable}"
                    + (accuracy is { } a ? $" ({a:P1})" : " (n/a)")
                    + $"  meanContextChars={(questions.Count == 0 ? 0 : totalChars / questions.Count)}"
                    + $"  withDistractors={addedAny}/{questions.Count}");
            }

            // A sweep whose context never grows measured one condition several times.
            var sizes = levels.Select(level => (long)level.GetType().GetProperty("meanContextChars")!
                .GetValue(level)!).Distinct().Count();
            if (options.Levels.Count > 1 && sizes == 1)
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
                levelSpec = options.Levels.Select(level => new { k = level.Distractors, gold = level.GoldFraction }),
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
        LongMemEvalEvidenceQuestion question, int distractorCount, int seed, out int distractorsAdded,
        double goldFraction = 1.0, Action<int, int>? reportGold = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(goldFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(goldFraction, 1.0);

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

        // P3. Gold COMPLETENESS, the mirror of the distractor sweep. Adding noise measured whether
        // wrong material hurts; dropping gold sessions measures whether a PARTIAL answer is as good
        // as a whole one. Recorded failures sit at RetrievedGoldCoverage 0.43-0.88, so real retrieval
        // lives in this regime rather than at the 1.0 both other sweeps held.
        var goldSessions = question.AnswerSessionIds
            .OrderBy(sessionId => sessionId, StringComparer.Ordinal)
            .ToList();
        // Ceiling, never floor: a fraction that rounds a single-gold-session question to zero would
        // make it unanswerable by construction and score the arm for a defect of the sampler.
        var keepCount = Math.Max(1, (int)Math.Ceiling(goldSessions.Count * goldFraction));
        var keptGold = goldSessions.Take(keepCount).ToHashSet(StringComparer.Ordinal);
        reportGold?.Invoke(keptGold.Count, goldSessions.Count);

        return question.Messages
            .Where(message =>
                keptGold.Contains(message.SourceSessionId) ||
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

        var counts = (Value("--distractor-sessions") ?? "0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) && parsed >= 0
                ? parsed
                : throw new ArgumentException("--distractor-sessions must be non-negative integers."))
            .Distinct().OrderBy(value => value).ToList();

        var fractions = (Value("--gold-fraction") ?? "1.0")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value =>
                double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && parsed > 0 && parsed <= 1
                    ? parsed
                    : throw new ArgumentException("--gold-fraction must be in (0, 1]."))
            .Distinct().OrderByDescending(value => value).ToList();

        // The cross product, so noise and completeness can be swept independently or together. The
        // two are different questions -- one asks whether wrong material hurts, the other whether a
        // partial answer is as good as a whole one -- and collapsing them would confound both.
        var levels = fractions
            .SelectMany(fraction => counts.Select(count => new SweepLevel(count, fraction)))
            .ToList();

        return new PrecisionOptions(
            datasetPath,
            ParsePositive(Value("--questions"), 10, "--questions"),
            ParsePositive(Value("--seed"), 42, "--seed"),
            levels,
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

    internal sealed record SweepLevel(int Distractors, double GoldFraction);

    private sealed record PrecisionOptions(
        string DatasetPath,
        int Questions,
        int Seed,
        IReadOnlyList<SweepLevel> Levels,
        string? OutputPath);
}
