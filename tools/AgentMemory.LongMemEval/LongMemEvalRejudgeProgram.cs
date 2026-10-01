using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Re-judges the answers a stored LongMemEval report already holds, with whatever judge the
/// environment names, and reports how often the two judges agree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Every score in a lineage is a judge's verdict. When the judge comes from the
/// same model family as the answer model it grades, a family bias would move every number and nothing
/// in the report would show it. Re-judging the stored answers costs one judge call per answer and no
/// extraction, recall or answering, so it is the cheapest check on the instrument itself.
/// </para>
/// <para>
/// <b>Nothing about grading is reimplemented.</b> The verdict comes from AgentEval's own
/// <see cref="LongMemEvalJudge"/>, under the judge options the original run recorded in its fingerprint
/// (verdict protocol, output ceiling, retries). Question text and gold answers come from the dataset
/// through AgentEval's loader, after the dataset's SHA-256 is checked against the one the report
/// recorded.
/// </para>
/// <para>
/// <b>Self-validating.</b> Run it once with the original judge (an A/A pass) before trusting a
/// cross-family comparison: the A/A agreement is the noise floor any other judge is read against.
/// </para>
/// </remarks>
internal static class LongMemEvalRejudgeProgram
{
    internal const string Option = "--rejudge";

    internal static readonly string[] KnownOptions = [Option, "--dataset", "--output", "--arms", "--limit"];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            LongMemEvalArgumentValidator.Validate(args, KnownOptions);
            var reportPath = Path.GetFullPath(Value(args, Option)
                ?? throw new ArgumentException($"{Option} requires a path to a stored report .json."));
            var datasetPath = Value(args, "--dataset")
                ?? throw new ArgumentException("--dataset <longmemeval_s_cleaned.json> is required.");
            if (!File.Exists(reportPath)) throw new FileNotFoundException("No such report.", reportPath);
            if (!File.Exists(datasetPath)) throw new FileNotFoundException("LongMemEval dataset not found.", datasetPath);

            var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath).ConfigureAwait(false))?.AsObject()
                ?? throw new InvalidDataException("The report is not a JSON object.");
            var stored = ReadStoredJudge(report);
            var datasetSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(datasetPath).ConfigureAwait(false)));
            if (!string.Equals(datasetSha, stored.DatasetSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The dataset's SHA-256 ({datasetSha}) is not the one the report recorded ({stored.DatasetSha256}).");
            }

            var armFilter = Value(args, "--arms")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // --limit N re-judges the first N answers of each arm: the one-item stage of the run protocol.
            var limit = Value(args, "--limit") is { } limitText
                ? int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit) && parsedLimit > 0
                    ? parsedLimit
                    : throw new ArgumentException("--limit must be a positive integer.")
                : int.MaxValue;
            var arms = ReadArms(report)
                .Where(arm => armFilter is null || armFilter.Contains(arm.Name, StringComparer.OrdinalIgnoreCase))
                .Select(arm => arm with { Items = arm.Items.Take(limit).ToArray() })
                .ToArray();
            if (arms.Length == 0)
            {
                throw new InvalidDataException(
                    "The report holds no stored answers to re-judge. Reports written before the shared judgment "
                    + "projection kept answers only for the prepared pair's arms.");
            }

            var options = LongMemEvalBenchmarkProtocol.CreateOptions(
                datasetPath,
                questions: null,
                seed: 42,
                stored.JudgeRetryAttempts,
                LongMemEvalEvidenceDetail.Identifiers,
                maxRelevantMessages: 30,
                stored.Protocol,
                judgeMaxOutputTokens: stored.JudgeMaxOutputTokens);
            var index = LongMemEvalEvidenceIndex.Load(datasetPath, options);

            var model = HarnessClients.Create();
            var judgeIdentity = model.JudgeIdentity;
            using var judgeClient = new LongMemEvalChatCallMeter(model.CreateJudgeClient());
            var judge = new LongMemEvalJudge(judgeClient, NullLogger<LongMemEvalJudge>.Instance);

            Console.WriteLine(
                $"longmemeval: re-judging {arms.Sum(arm => arm.Items.Count)} stored answers ({string.Join(", ", arms.Select(arm => $"{arm.Name} {arm.Items.Count}"))}); "
                + $"recorded judge {stored.JudgeModel}, new judge {judgeIdentity}, protocol {stored.Protocol}, "
                + $"ceiling {stored.JudgeMaxOutputTokens} tokens.");

            var results = new List<ArmAgreement>();
            foreach (var arm in arms)
            {
                var rejudged = new List<RejudgedItem>();
                foreach (var item in arm.Items)
                {
                    var question = ToQuestion(index.GetByQuestionId(item.QuestionId));
                    var judgment = await judge.JudgeAsync(item.AgentResponse, question, options).ConfigureAwait(false);
                    rejudged.Add(new RejudgedItem(
                        item.QuestionId, question.QuestionType, item.Correct, judgment.Correct,
                        judgment.Status.ToString(), judgment.Explanation));
                }

                results.Add(ArmAgreement.From(arm.Name, rejudged));
            }

            var calls = judgeClient.Snapshot();
            var destination = Path.GetFullPath(Value(args, "--output")
                ?? Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath) + "-rejudge.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var output = new
            {
                schemaVersion = 1,
                generatedAtUtc = DateTimeOffset.UtcNow,
                sourceReport = Path.GetFileName(reportPath),
                recordedJudge = stored.JudgeModel,
                newJudge = judgeIdentity,
                verdictProtocol = stored.Protocol.ToString(),
                judgeMaxOutputTokens = stored.JudgeMaxOutputTokens,
                judgeRetryAttempts = stored.JudgeRetryAttempts,
                datasetSha256 = datasetSha,
                judgeCalls = new { calls.Calls, calls.Failures, durationMs = calls.Duration.TotalMilliseconds },
                arms = results,
            };
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }))
                .ConfigureAwait(false);

            foreach (var arm in results)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {arm.Arm}: {arm.Agreed}/{arm.Compared} agree ({arm.AgreementPercent:0.0} %), kappa {arm.CohensKappa:0.00}; "
                    + $"recorded correct {arm.RecordedCorrect}, new correct {arm.NewCorrect}, new inconclusive {arm.NewInconclusive}"));
            }

            Console.WriteLine($"longmemeval: re-judge written to {destination}");
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or InvalidDataException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"longmemeval --rejudge: {exception.Message}");
            return 2;
        }
    }

    internal sealed record StoredJudge(
        string JudgeModel,
        string DatasetSha256,
        JudgeVerdictProtocol Protocol,
        int JudgeMaxOutputTokens,
        int JudgeRetryAttempts);

    internal sealed record StoredItem(string QuestionId, bool Correct, string AgentResponse);

    internal sealed record StoredArm(string Name, IReadOnlyList<StoredItem> Items);

    internal sealed record RejudgedItem(
        string QuestionId,
        string QuestionType,
        bool Recorded,
        bool? New,
        string NewStatus,
        string? NewExplanation);

    /// <summary>Agreement between the recorded and the new verdicts for one arm.</summary>
    /// <remarks>
    /// An inconclusive new verdict is not a disagreement and not an agreement: it is counted apart and
    /// left out of agreement and kappa, so a judge that refuses to answer cannot look like one that agrees.
    /// </remarks>
    internal sealed record ArmAgreement(
        string Arm,
        int Compared,
        int Agreed,
        double AgreementPercent,
        double CohensKappa,
        int RecordedCorrect,
        int NewCorrect,
        int NewInconclusive,
        IReadOnlyList<RejudgedItem> Disagreements,
        IReadOnlyList<RejudgedItem> Items)
    {
        internal static ArmAgreement From(string arm, IReadOnlyList<RejudgedItem> items)
        {
            var decided = items.Where(item => item.New is not null).ToArray();
            var agreed = decided.Count(item => item.Recorded == item.New);
            return new ArmAgreement(
                arm,
                decided.Length,
                agreed,
                decided.Length == 0 ? 0 : 100.0 * agreed / decided.Length,
                Kappa(decided.Select(item => (item.Recorded, item.New!.Value)).ToArray()),
                items.Count(item => item.Recorded),
                decided.Count(item => item.New == true),
                items.Count - decided.Length,
                decided.Where(item => item.Recorded != item.New).ToArray(),
                items);
        }

        /// <summary>Cohen's kappa for two binary raters; 1 when both raters are constant and equal.</summary>
        internal static double Kappa(IReadOnlyList<(bool A, bool B)> pairs)
        {
            if (pairs.Count == 0) return 0;
            double n = pairs.Count;
            var observed = pairs.Count(pair => pair.A == pair.B) / n;
            var aYes = pairs.Count(pair => pair.A) / n;
            var bYes = pairs.Count(pair => pair.B) / n;
            var expected = aYes * bYes + (1 - aYes) * (1 - bYes);
            return expected >= 1 ? (observed >= 1 ? 1 : 0) : (observed - expected) / (1 - expected);
        }
    }

    /// <summary>The judge options the original run recorded, read from its fingerprint.</summary>
    internal static StoredJudge ReadStoredJudge(JsonObject report)
    {
        var fingerprint = report["fingerprint"]?.AsObject()
            ?? throw new InvalidDataException("The report has no fingerprint, so its judge options are unknown.");
        var protocolText = fingerprint["judgeVerdictProtocol"]?.GetValue<string>()
            ?? throw new InvalidDataException("The report does not record its judge verdict protocol.");
        if (!Enum.TryParse<JudgeVerdictProtocol>(protocolText, ignoreCase: true, out var protocol))
            throw new InvalidDataException($"Unknown judge verdict protocol '{protocolText}'.");

        // The prepared pair records the ceiling as a number; the reference arm only inside its
        // judgeRequest label ("...-4096-tokens"). Either is the run's own record; neither is a guess.
        int? ceiling = fingerprint["judgeMaxOutputTokens"]?.GetValue<int>();
        if (ceiling is null && fingerprint["judgeRequest"]?.GetValue<string>() is { } request &&
            Regex.Match(request, @"-(\d+)-tokens$") is { Success: true } match)
        {
            ceiling = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return new StoredJudge(
            fingerprint["judgeModel"]?.GetValue<string>() ?? "unknown",
            fingerprint["datasetSha256"]?.GetValue<string>()
                ?? throw new InvalidDataException("The report does not record its dataset's SHA-256."),
            protocol,
            ceiling ?? throw new InvalidDataException("The report does not record its judge output ceiling."),
            fingerprint["judgeRetryAttempts"]?.GetValue<int>() ?? 2);
    }

    /// <summary>The stored answers, per arm, in either report shape.</summary>
    internal static IReadOnlyList<StoredArm> ReadArms(JsonObject report)
    {
        var arms = new List<StoredArm>();
        if (report["arms"] is JsonObject pair)
        {
            foreach (var (name, node) in pair)
            {
                if (node?["judgments"] is JsonArray judgments) arms.Add(new StoredArm(name, Items(judgments)));
            }
        }
        else if (report["judgments"] is JsonArray reference)
        {
            var name = report["referenceArm"]?["arm"]?.GetValue<string>() ?? "reference";
            arms.Add(new StoredArm(name, Items(reference)));
        }

        return arms;
    }

    private static IReadOnlyList<StoredItem> Items(JsonArray judgments) =>
        judgments
            .OfType<JsonObject>()
            .Where(judgment => judgment["agentResponse"] is not null)
            .Select(judgment => new StoredItem(
                judgment["QuestionId"]!.GetValue<string>(),
                judgment["Correct"]!.GetValue<bool>(),
                judgment["agentResponse"]!.GetValue<string>()))
            .ToArray();

    private static ExternalBenchmarkQuestion ToQuestion(LongMemEvalEvidenceQuestion indexed) => new()
    {
        QuestionId = indexed.QuestionId,
        QuestionType = indexed.QuestionType,
        Question = indexed.Question,
        GoldAnswer = indexed.GoldAnswer,
        QuestionDate = indexed.QuestionDate,
        IsAbstention = indexed.IsAbstention
    };

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }
}
