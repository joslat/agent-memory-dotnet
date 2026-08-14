using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 26.3. The first instrument that can say anything about <b>prospective</b> memory — and it asks the
/// cheapest question first: are these questions answerable at all with perfect context?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists now and could not before.</b> LongMemEval-S carries no prospective questions at
/// any sample size, and its conversation dates live only in metadata, so nothing forces an ingesting
/// system to place messages in time. AgentEval 0.21.0-beta ships a <b>time-grounded corpus</b> with
/// three question families — <c>tg-asof</c> (what was true then), <c>tg-current</c> (what is true now)
/// and <c>tg-prospective</c> (what becomes true later) — which is exactly the third ask of prompt 04.
/// </para>
/// <para>
/// <b>Ceiling before treatment.</b> Running the oracle first is the discipline that closed 27.3: a
/// question the model answers wrongly with the gold evidence in front of it cannot be fixed by any
/// memory system, and buying a corpus build to discover that would be the expensive way round. No
/// Neo4j, no extraction, no corpus — answer and judge calls only.
/// </para>
/// <para>
/// <b>Four questions per family.</b> One question is 25 accuracy points. This can establish that a
/// family is <i>unanswerable</i>, or that it is <i>reachable</i>; it cannot rank two memory systems,
/// and no percentage from it should be quoted as an accuracy.
/// </para>
/// </remarks>
internal static class LongMemEvalTimeGroundedOracleProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

            using var judgeCalls = new LongMemEvalChatCallMeter(
                azure.GetChatClient(deployment).AsIChatClient());
            using var answerCalls = new LongMemEvalChatCallMeter(
                azure.GetChatClient(deployment).AsIChatClient());

            Console.WriteLine(
                "longmemeval: time-grounded oracle -- gold context only, no Neo4j, no extraction, "
                + "no corpus build.");

            // The corpus is embedded in AgentEval, so no dataset path is needed or wanted: passing one
            // would point this at LongMemEval-S, which has no prospective questions at all.
            var runner = LongMemEvalBenchmarkRunner.Create(judgeCalls, datasetPath: null);
            var result = await runner.RunTimeGroundedOracleAsync(answerCalls).ConfigureAwait(false);

            var byFamily = result.QuestionResults
                .Where(question => question.QuestionId is not null)
                .GroupBy(question => Family(question.QuestionId!), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine();
            foreach (var family in byFamily)
            {
                var total = family.Count();
                var correct = family.Count(question => question.Correct == true);
                Console.WriteLine(
                    $"  {family.Key,-16} {correct}/{total}"
                    + (correct == 0 ? "   <== ORACLE-IMPOSSIBLE: no memory system can reach these" : string.Empty));
            }

            var artifacts = Value(args, "--artifacts") ?? Path.Combine("artifacts", "evaluation");
            Directory.CreateDirectory(artifacts);
            var path = Path.Combine(
                artifacts, $"time-grounded-oracle-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(
                new
                {
                    probe = "time-grounded-oracle",
                    task = "26.3",
                    questions = result.QuestionResults.Count,
                    note = "Four questions per family: one question is 25 accuracy points. This can "
                        + "establish that a family is unanswerable or that it is reachable. It cannot "
                        + "rank two memory systems, and no percentage here is an accuracy.",
                    families = byFamily.Select(family => new
                    {
                        family = family.Key,
                        total = family.Count(),
                        correct = family.Count(question => question.Correct == true),
                    }),
                    questionResults = result.QuestionResults.Select(question => new
                    {
                        question.QuestionId,
                        question.Correct,
                        status = question.JudgeStatus?.ToString(),
                    }),
                },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"longmemeval: wrote {path}");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"longmemeval: time-grounded oracle failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    /// <summary>The family prefix, e.g. <c>tg-prospective-002</c> → <c>tg-prospective</c>.</summary>
    private static string Family(string questionId)
    {
        var lastDash = questionId.LastIndexOf('-');
        return lastDash <= 0 ? questionId : questionId[..lastDash];
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}
