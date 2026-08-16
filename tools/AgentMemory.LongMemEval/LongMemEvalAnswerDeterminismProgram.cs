using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 27.2. Asks whether the <b>answer</b> model can be pinned on this deployment, by re-issuing the
/// identical answer call under three option sets and counting how many distinct texts come back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the highest-value probe available.</b> Across constant-configuration repeats of one
/// 50-question set, <b>13 of 14</b> questions that flipped verdict did so with byte-identical
/// retrieval — same corpus, same config, same <c>ItemsRetrieved</c>. That is not memory and not
/// retrieval; it is the answer model disagreeing with itself. The cause is ours and it is
/// configuration: <see cref="AgentMemoryLongMemEvalAdapter"/> issues the answer call with <b>no
/// <see cref="ChatOptions"/> at all</b>, so it runs at the provider default temperature. If any option
/// set here collapses the distinct-text count to one, a share of the noise floor that currently blunts
/// every measurement this project takes is removable.
/// </para>
/// <para>
/// <b>It compares text, not verdicts, and so spends no judge calls.</b> Determinism is a property of
/// the returned string. Routing this through the judge would add cost, add the judge's own
/// nondeterminism to the measurement, and detect strictly less: two textually different answers that
/// happen to earn the same verdict are still evidence the model is unpinned.
/// </para>
/// <para>
/// <b>Void witness.</b> If the baseline arm returns a single distinct text on every probe question,
/// this prompt has no observable entropy and the probe <b>cannot discriminate</b> — a seeded arm would
/// then look deterministic whether or not the seed did anything. That prints VOID and exits non-zero,
/// because the alternative is to report an unfalsifiable success.
/// </para>
/// </remarks>
internal static class LongMemEvalAnswerDeterminismProgram
{
    private const int SeedValue = 12345;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

            using var meter = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            var benchmarkOptions = LongMemEvalBenchmarkProtocol.CreateOptions(
                options.DatasetPath,
                options.Questions,
                options.Seed,
                judgeRetryAttempts: 0,
                LongMemEvalEvidenceDetail.Identifiers,
                maxRelevantMessages: 30);
            var evidenceIndex = LongMemEvalEvidenceIndex.Load(options.DatasetPath, benchmarkOptions);

            var selected = Select(evidenceIndex.Questions, options.QuestionIds, options.ProbeQuestions);
            if (selected.Count == 0)
            {
                Console.Error.WriteLine(
                    "longmemeval: no questions selected for the determinism probe.");
                return 2;
            }

            // Every arm answers the SAME prompt built the SAME way as the shipping adapter. A probe on
            // a toy prompt would measure the toy: reasoning-model determinism is prompt-length and
            // prompt-shape sensitive, so a short synthetic question could look pinned while the real
            // 30-message answer call is not.
            var prompts = selected.ToDictionary(
                question => question.QuestionId,
                question => AgentMemoryLongMemEvalAdapter.BuildAnswerPrompt(
                    LongMemEvalContextPrecisionProgram.BuildContext(
                        question, distractorCount: 0, options.Seed, out _),
                    question.InvocationPrompt,
                    question.QuestionDate),
                StringComparer.Ordinal);

            var arms = new (string Name, Func<ChatOptions?> Options)[]
            {
                // Exactly what ships today: no options object at all.
                ("baseline", () => null),
                ("seeded", () => new ChatOptions { Seed = SeedValue }),
                ("temperature-zero", () => new ChatOptions { Temperature = 0 }),
                // Some deployments honour a seed only when sampling is described explicitly. Cheap to
                // include, and it separates "seed ignored" from "seed ignored unless temperature is set".
                ("seeded-temperature-one", () => new ChatOptions { Seed = SeedValue, Temperature = 1 }),
            };

            Console.WriteLine(
                $"longmemeval: answer-determinism probe over {selected.Count} question(s) x "
                + $"{options.Repeats} repeats x {arms.Length} arms "
                + $"= {selected.Count * options.Repeats * arms.Length} answer calls, no judge calls.");

            var results = new List<ArmResult>();
            foreach (var (armName, armOptions) in arms)
            {
                var perQuestion = new List<QuestionResult>();
                string? rejection = null;

                foreach (var question in selected)
                {
                    var texts = new List<string>();
                    for (var repeat = 0; repeat < options.Repeats && rejection is null; repeat++)
                    {
                        try
                        {
                            var response = await meter.GetResponseAsync(
                                [
                                    new ChatMessage(ChatRole.System, AgentMemoryLongMemEvalAdapter.SystemPrompt),
                                    new ChatMessage(ChatRole.User, prompts[question.QuestionId]),
                                ],
                                armOptions()).ConfigureAwait(false);
                            texts.Add((response.Text ?? string.Empty).Trim());
                        }
                        catch (Exception exception)
                        {
                            // A provider that refuses the option set is a RESULT, not a crash: this
                            // deployment already rejects `temperature: 0` on the extraction path, which
                            // is why ProviderCompatibleExtractionChatClient exists. Record the refusal
                            // and move to the next arm rather than failing the run.
                            rejection = $"{exception.GetType().Name}: {Summarize(exception.Message)}";
                        }
                    }

                    if (rejection is not null) break;

                    perQuestion.Add(new QuestionResult(
                        question.QuestionId,
                        texts.Distinct(StringComparer.Ordinal).Count(),
                        texts.Count,
                        options.IncludeText ? texts : null));
                }

                results.Add(new ArmResult(armName, rejection, perQuestion));
                var summary = rejection is not null
                    ? $"REJECTED ({rejection})"
                    : $"{perQuestion.Sum(q => q.DistinctTexts)} distinct / {perQuestion.Sum(q => q.Calls)} calls";
                Console.WriteLine($"  {armName,-24} {summary}");
            }

            var baseline = results.Single(arm => arm.Name == "baseline");
            if (baseline.Rejection is not null)
            {
                Console.Error.WriteLine(
                    "longmemeval: VOID — the baseline arm itself was rejected, so there is no reference "
                    + "to compare the pinned arms against.");
                return 3;
            }

            var baselineNondeterministic = baseline.Questions.Any(q => q.DistinctTexts > 1);
            var verdict = Verdict(baselineNondeterministic, results, options.Repeats);

            Console.WriteLine();
            Console.WriteLine(verdict.Line);

            var artifact = new
            {
                probe = "answer-determinism",
                task = "27.2",
                deployment = "(not recorded — deployment names are secrets)",
                repeats = options.Repeats,
                seed = SeedValue,
                totalAnswerCalls = meter.Snapshot().CompletedCalls,
                baselineNondeterministic,
                verdict = verdict.Code,
                recommendation = verdict.Line,
                arms = results,
            };
            WriteArtifact(options.ArtifactsDirectory, artifact);

            return verdict.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: answer-determinism probe failed: {exception.Message}");
            return 1;
        }
    }

    private static (string Code, string Line, int ExitCode) Verdict(
        bool baselineNondeterministic, List<ArmResult> results, int repeats)
    {
        if (!baselineNondeterministic)
        {
            return ("void",
                $"VOID: the baseline arm returned ONE distinct text on every question across {repeats} "
                + "repeats, so this prompt shows no entropy and the probe cannot discriminate. A seeded "
                + "arm would look deterministic here whether or not the seed did anything. Re-run with "
                + "--repeats higher or different --question-ids before drawing any conclusion.",
                3);
        }

        var candidates = results
            .Where(arm => arm.Name != "baseline" && arm.Rejection is null && arm.Questions.Count > 0)
            .ToList();

        var pinned = candidates
            .Where(arm => arm.Questions.All(q => q.DistinctTexts == 1))
            .Select(arm => arm.Name)
            .ToList();

        if (pinned.Count > 0)
        {
            return ("pinnable",
                $"PINNABLE: the baseline disagrees with itself, and [{string.Join(", ", pinned)}] "
                + "returned a single distinct text on every question. Wire the winning option set into "
                + "the adapter's answer call — a share of the noise floor is removable.",
                0);
        }

        // Between "fully pinned" and "no effect" there is a real third state, and the first run of this
        // probe landed in it: 8 distinct of 8 on the baseline against 3-4 of 8 with a seed. Collapsing
        // that to IRREDUCIBLE would discard a measured, reproducible halving of answer variance and
        // would have closed 27.2 with the wrong conclusion.
        var baselineDistinct = results.Single(a => a.Name == "baseline").Questions.Sum(q => q.DistinctTexts);
        var baselineCalls = results.Single(a => a.Name == "baseline").Questions.Sum(q => q.Calls);
        var best = candidates
            .Select(arm => (arm.Name, Distinct: arm.Questions.Sum(q => q.DistinctTexts)))
            .OrderBy(arm => arm.Distinct)
            .FirstOrDefault();

        if (best.Name is not null && best.Distinct < baselineDistinct)
        {
            return ("partially-pinnable",
                $"PARTIALLY PINNABLE: no option set reached full determinism, but '{best.Name}' cut "
                + $"distinct answers from {baselineDistinct} to {best.Distinct} across {baselineCalls} "
                + "calls. The provider honours the option without guaranteeing it. Worth wiring as an "
                + "OPT-IN — it narrows the noise band, but it does not license calling a run reproducible.",
                0);
        }

        return ("irreducible",
            "IRREDUCIBLE: the baseline disagrees with itself and NO option set reduced it. On this "
            + "deployment the answer-model noise floor cannot be removed by configuration, so it must "
            + "be handled statistically (repeat runs, report a band) rather than eliminated.",
            0);
    }

    private static List<LongMemEvalEvidenceQuestion> Select(
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions, string[] ids, int take)
    {
        if (ids.Length > 0)
        {
            return questions
                .Where(question => ids.Contains(question.QuestionId, StringComparer.Ordinal))
                .ToList();
        }

        // Longest gold context first. Entropy rises with prompt length and with how much the model has
        // to select from, so the longest prompts are where the shipping answer call is least pinned —
        // and a probe that voids on a short prompt has told us nothing about the real one.
        return questions
            .OrderByDescending(question => question.Messages.Count)
            .ThenBy(question => question.QuestionId, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    private static string Summarize(string message) =>
        message.Length <= 200 ? message : message[..200] + "...";

    private static void WriteArtifact(string directory, object artifact)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"answer-determinism-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            artifact, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"longmemeval: wrote {path}");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");

    private static ProbeOptions Parse(string[] args)
    {
        var dataset = LongMemEvalDatasetLocator.Resolve(
                Value(args, "--dataset"), Environment.GetEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "No LongMemEval dataset found. Pass --dataset or set LONGMEMEVAL_DATASET.");
        return new ProbeOptions(
            dataset,
            int.TryParse(Value(args, "--questions"), out var questions) ? questions : 50,
            int.TryParse(Value(args, "--seed"), out var seed) ? seed : 42,
            int.TryParse(Value(args, "--repeats"), out var repeats) ? repeats : 4,
            int.TryParse(Value(args, "--probe-questions"), out var probe) ? probe : 2,
            (Value(args, "--question-ids") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            args.Contains("--include-text", StringComparer.Ordinal),
            Value(args, "--artifacts") ?? Path.Combine("artifacts", "evaluation"));
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed record ProbeOptions(
        string DatasetPath, int Questions, int Seed, int Repeats, int ProbeQuestions,
        string[] QuestionIds, bool IncludeText, string ArtifactsDirectory);

    private sealed record QuestionResult(
        string QuestionId, int DistinctTexts, int Calls, IReadOnlyList<string>? Texts);

    private sealed record ArmResult(string Name, string? Rejection, List<QuestionResult> Questions);
}
