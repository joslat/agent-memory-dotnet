using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.External.TypedMemEval;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Re-grades a stored TypedMemEval artifact under the currently referenced judge, without re-running
/// extraction, retrieval, or answering.
/// </summary>
/// <remarks>
/// <para>
/// <b>The situation this exists for.</b> The Bitemporal vertical shipped with no judge body, so
/// every bitemporal number on record was graded by a judge that graded the gold answer's
/// justification rather than its value. The model answers were not affected — the judge is strictly
/// downstream of answering, verified in code — so the correct repair is to re-grade the stored
/// answers, not to re-run four hours of pipeline per arm.
/// </para>
/// <para>
/// <b>It replays through AgentEval's own runner.</b> Question selection, judge body, typed-outcome
/// derivation and attribution accounting are all their code, reached through
/// <see cref="TypedMemEvalReplayAdapter"/>. Nothing about grading is reimplemented here, so this
/// cannot drift from the path that produces every other number we cite, and it inherits whatever the
/// new package changes.
/// </para>
/// <para>
/// <b>Self-validating, which is why it can be trusted before the fixed judge exists.</b> Re-grading
/// an artifact under the SAME judge that produced it must reproduce its verdicts. That check costs
/// one judge pass and no new package. An instrument that cannot reproduce a known result has no
/// business re-anchoring a baseline, and this prints the agreement rate so the question is answered
/// rather than assumed.
/// </para>
/// <para>
/// <b>Sampling is read from the artifact's own provenance sidecar</b>, never re-specified by hand: a
/// re-grade that selected a different question set would silently compare two different corpora
/// slices and report it as a judge effect.
/// </para>
/// </remarks>
internal static class TypedMemEvalRegradeProgram
{
    internal static readonly string[] KnownOptions = ["--regrade"];

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            LongMemEvalArgumentValidator.Validate(args, KnownOptions);

            var reportPath = Value(args, "--regrade")
                ?? throw new ArgumentException("--regrade requires a path to a stored report .json.");
            reportPath = Path.GetFullPath(reportPath);
            if (!File.Exists(reportPath))
                throw new FileNotFoundException($"No such report: {reportPath}");

            var sidecarPath = Path.ChangeExtension(reportPath, null) + ".provenance.json";
            if (!File.Exists(sidecarPath))
            {
                // Refused rather than defaulted. The sidecar carries the seed and question cap; a
                // guessed sampling would re-grade a DIFFERENT question set and the difference would
                // present as a judge effect, which is the one conclusion this tool exists to support.
                throw new FileNotFoundException(
                    $"No provenance sidecar beside the report ({Path.GetFileName(sidecarPath)}). " +
                    "Sampling cannot be reconstructed safely without it.");
            }

            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            using var sidecar = JsonDocument.Parse(File.ReadAllText(sidecarPath));

            // Kept in artifact ORDER. Twelve of the sixty bitemporal questions share their text with
            // another question that has a DIFFERENT gold answer (tme-bit-007 / tme-bit-037 both ask
            // about Colm Whitaker in February and answer Lowick and Marchmont), because each question
            // is asked against its own injected history. Keying by text would hand 12 of 60 questions
            // an answer written for a different question -- and grade it.
            var rows = report.RootElement.GetProperty("QuestionResults");
            var storedRows = new List<(string Question, string Answer)>();
            string? modelId = null;
            foreach (var row in rows.EnumerateArray())
            {
                storedRows.Add((
                    row.GetProperty("Question").GetString() ?? string.Empty,
                    row.GetProperty("AgentResponse").GetString() ?? string.Empty));
            }

            var sampling = sidecar.RootElement.GetProperty("sampling");
            var verticalSlug = sidecar.RootElement.GetProperty("vertical").GetString()!;
            // Resolved through the descriptor table, the same lookup the run verb uses, so a slug
            // this build does not know fails here rather than silently grading a different vertical.
            var vertical = (TypedMemEvalVerticals.All.FirstOrDefault(candidate =>
                    string.Equals(candidate.Slug, verticalSlug, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"The sidecar names vertical '{verticalSlug}', which this build does not know."))
                .Vertical;
            var armToken = sidecar.RootElement.GetProperty("arm").GetProperty("token").GetString();

            // CORPUS IDENTITY GATE, checked before a single judge call.
            //
            // AgentEval redraws corpora keeping the question_id set 100% IDENTICAL with ZERO
            // byte-identical items, and neither corpus_id nor revision moves either -- corpus_sha256
            // is the ONLY distinguishing field. For bitemporal, 27 of 60 items keep the SAME question
            // text and carry a DIFFERENT gold.
            //
            // That defeats the protection this program already had. The replay is position-keyed and
            // verifies the question TEXT at each position, which catches a redraw that reworded
            // anything -- and catches nothing at all when the text is stable and only the gold moved.
            // Re-grading across such a redraw would replay the right answers against the wrong keys
            // and report an agreement rate that means nothing.
            var storedCorpusSha =
                report.RootElement.TryGetProperty("TypedOutcomes", out var outcomes)
                && outcomes.ValueKind == JsonValueKind.Object
                && outcomes.TryGetProperty("CorpusSha256", out var sha)
                    ? sha.GetString()
                    : null;
            var currentCorpusSha = CurrentCorpusSha(verticalSlug);

            if (storedCorpusSha is null)
            {
                // Not fatal, and deliberately so: artifacts predating provenance capture are still
                // re-gradable. But it is the one case where nothing can be verified, so it is said
                // out loud rather than passing quietly.
                Console.WriteLine(
                    "regrade: WARNING the stored artifact records no corpus sha, so corpus identity " +
                    "CANNOT be verified. If it predates the current corpus, the replay may grade " +
                    "against different gold behind identical question text.");
            }
            else if (currentCorpusSha is not null &&
                     !string.Equals(storedCorpusSha, currentCorpusSha, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"regrade: ABORT — corpus mismatch. The artifact was produced against " +
                    $"{storedCorpusSha[..16]}… and this build carries {currentCorpusSha[..16]}…. " +
                    "Question ids and text are NOT sufficient to detect this: corpora are redrawn " +
                    "keeping ids identical, and gold can move behind unchanged text. Re-grade with " +
                    "the package the artifact was produced against.");
                return 5;
            }


            Console.WriteLine(
                $"regrade: source {Path.GetFileName(reportPath)} — vertical {verticalSlug}, " +
                $"arm {armToken}, {storedRows.Count} stored answers replayed in artifact order");

            var facade = new TypedMemEvalOptions
            {
                MaxQuestions = Nullable(sampling, "maxQuestions"),
                RandomSeed = Nullable(sampling, "randomSeed"),
                AnswerSeed = Nullable(sampling, "answerSeed"),
                ControlArm = sampling.GetProperty("control").GetBoolean(),
                TemporalGrounding = sampling.GetProperty("control").GetBoolean()
                    ? TemporalGroundingMode.TimestampsAndText
                    : null,
            };

            var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
            var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
            var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
            var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
            using var judgeChatClient = new LongMemEvalChatCallMeter(
                azureClient.GetChatClient(deployment).AsIChatClient());

            var adapter = new TypedMemEvalReplayAdapter(storedRows, modelId);
            var runner = new TypedMemEvalRunner(judgeChatClient);
            var result = await runner.RunAsync(adapter, vertical, facade).ConfigureAwait(false);

            if (adapter.OrderingMismatches.Count > 0)
            {
                // The runner selected or ordered questions differently from the stored artifact, so
                // positional replay is pairing answers with the wrong questions. That yields a
                // complete, plausible, entirely meaningless score -- the worst possible outcome, and
                // the reason the position assumption is verified rather than trusted.
                Console.Error.WriteLine(
                    $"regrade: ABORT — {adapter.OrderingMismatches.Count} position(s) did not match " +
                    "the stored question order, so answers would be paired with the wrong questions. " +
                    "First: " + Truncate(adapter.OrderingMismatches[0]));
                return 4;
            }

            if (adapter.UnmatchedQuestions.Count > 0)
            {
                // An unmatched question is graded as an empty answer, which scores as WRONG rather
                // than as MISSING and would quietly lower the re-graded baseline -- corrupting the
                // very number this exists to establish. Fail loudly instead.
                Console.Error.WriteLine(
                    $"regrade: ABORT — {adapter.UnmatchedQuestions.Count} question(s) had no stored " +
                    "answer, so the re-graded score would be understated. First: " +
                    Truncate(adapter.UnmatchedQuestions[0]));
                return 3;
            }

            // Persisted FIRST, and the ordering is the fix for a real loss: Agreement() used to run
            // before this and threw on a null verdict, destroying an artifact that had already cost a
            // full judge pass. A cheap derived metric must never stand between an expensive result and
            // the disk. Agreement is written into the sidecar afterwards, by amendment.
            var destination = Persist(result, reportPath, sidecarPath, armToken, agreement: null);
            var agreement = Agreement(report.RootElement, result);
            AmendSidecarWithAgreement(destination, agreement);

            Console.WriteLine(
                $"regrade: matched {adapter.Matched} answers; " +
                (agreement.Unscored > 0
                    ? $"{agreement.Unscored} question(s) returned NO VERDICT (validity issue, not a score); "
                    : string.Empty) +
                $"judge agreement with the stored verdicts {agreement.Agreed}/{agreement.Compared} " +
                $"({(agreement.Compared == 0 ? 0 : (double)agreement.Agreed / agreement.Compared):P1})");
            Console.WriteLine($"regrade: report {destination}");
            Console.WriteLine(
                "regrade: NOTE — agreement is the instrument check when re-grading under the SAME " +
                "judge, and the FINDING when re-grading under a different one. Which of the two this " +
                "run was depends on the AgentEval package referenced at build time: " +
                AgentEvalVersion());
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or JsonException)
        {
            Console.Error.WriteLine($"regrade: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Per-question verdict agreement between the stored artifact and the re-grade.</summary>
    /// <remarks>
    /// Compared by <c>QuestionId</c>, not by position: the runner selects questions itself, and a
    /// positional comparison would silently pair different questions if selection ever changed.
    /// </remarks>
    private static (int Compared, int Agreed, int Unscored) Agreement(
        JsonElement stored, object regraded)
    {
        var storedById = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in stored.GetProperty("QuestionResults").EnumerateArray())
        {
            var id = row.GetProperty("QuestionId").GetString();
            if (id is null) continue;

            // The SAME null-verdict case the re-graded side below already handles, and it was missed
            // here -- an asymmetry that would have thrown AFTER the judge pass was paid for, which is
            // exactly the cheap-thing-destroys-expensive-result failure the persist-before-diagnostics
            // ordering exists to prevent. A stored row with no verdict is simply not comparable.
            var storedVerdict = row.GetProperty("Correct");
            if (storedVerdict.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            storedById[id] = storedVerdict.GetBoolean();
        }

        // Re-serialized rather than reflected over: the result type is AgentEval's and its shape is
        // theirs to change, so reading it the same way the artifact is read keeps one parsing story.
        using var fresh = JsonDocument.Parse(JsonSerializer.Serialize(regraded));
        var compared = 0;
        var agreed = 0;
        var unscored = 0;
        foreach (var row in fresh.RootElement.GetProperty("QuestionResults").EnumerateArray())
        {
            var id = row.GetProperty("QuestionId").GetString();
            if (id is null || !storedById.TryGetValue(id, out var before)) continue;

            // `Correct` is NULL when the judge returned no verdict at all. That is neither agreement
            // nor disagreement, and folding it into either would move the agreement rate for a reason
            // that has nothing to do with the judge's opinion. Counted on its own instead, because a
            // re-grade carrying unscored questions is a validity question before it is a score.
            var verdict = row.GetProperty("Correct");
            if (verdict.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                unscored++;
                continue;
            }

            compared++;
            if (before == verdict.GetBoolean()) agreed++;
        }

        return (compared, agreed, unscored);
    }

    private static string Persist(
        object result,
        string sourceReportPath,
        string sourceSidecarPath,
        string? armToken,
        (int Compared, int Agreed, int Unscored)? agreement)
    {
        var stamp = DateTimeOffset.UtcNow;
        var name =
            Path.GetFileNameWithoutExtension(sourceReportPath) +
            $"-regrade-{stamp:yyyyMMddTHHmmssZ}.json";
        var destination = Path.GetFullPath(Path.Combine("artifacts", "evaluation", name));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(
            destination,
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);

        var provenance = new
        {
            schema = "typedmemeval-regrade-provenance/1",
            report = Path.GetFileName(destination),
            regradedAtUtc = stamp.ToString("O", CultureInfo.InvariantCulture),
            // The whole point of the artifact: which answers were graded, and by which judge.
            source = new
            {
                report = Path.GetFileName(sourceReportPath),
                sidecar = Path.GetFileName(sourceSidecarPath),
                arm = armToken,
            },
            judge = new
            {
                agentEvalVersion = AgentEvalVersion(),
                // Null until amended in: the artifact is written before agreement is computed, so a
                // crash in the diagnostic cannot cost the judge pass that produced the artifact.
                agreementWithStored = agreement?.Agreed,
                agreementCompared = agreement?.Compared,
                unscoredQuestions = agreement?.Unscored,
            },
            // Stated in the artifact so no later reader has to infer it: answers were REPLAYED. No
            // extraction, retrieval or answering ran, so retrieval-side diagnostics in the body
            // describe the ORIGINAL run and must not be read as fresh measurements.
            answersReplayed = true,
        };
        File.WriteAllText(
            Path.ChangeExtension(destination, null) + ".provenance.json",
            JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
        return destination;
    }

    /// <summary>Writes the agreement figures into the sidecar after the artifact is safely on disk.</summary>
    /// <remarks>
    /// A second small write rather than a delayed first one. The expensive thing is the judge pass;
    /// once it exists it must reach disk before anything that can throw runs against it.
    /// </remarks>
    private static void AmendSidecarWithAgreement(
        string reportPath, (int Compared, int Agreed, int Unscored) agreement)
    {
        var sidecarPath = Path.ChangeExtension(reportPath, null) + ".provenance.json";
        var node = JsonNode.Parse(File.ReadAllText(sidecarPath))!;
        var judge = node["judge"]!;
        judge["agreementWithStored"] = agreement.Agreed;
        judge["agreementCompared"] = agreement.Compared;
        judge["unscoredQuestions"] = agreement.Unscored;
        File.WriteAllText(
            sidecarPath,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>The sha256 of the corpus this build actually carries, or null if not found.</summary>
    /// <remarks>
    /// Hashed from the embedded resource rather than read from a manifest, so it is the bytes the run
    /// would use and not a claim about them. Null when the resource cannot be located, which is
    /// treated as "cannot verify" rather than as "matches".
    /// </remarks>
    private static string? CurrentCorpusSha(string verticalSlug)
    {
        var assembly = typeof(TypedMemEvalRunner).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(resource =>
            resource.Contains($".{verticalSlug}.", StringComparison.OrdinalIgnoreCase)
            && resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !resource.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buffer.ToArray()))
            .ToLowerInvariant();
    }

    private static string AgentEvalVersion() =>
        typeof(TypedMemEvalRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TypedMemEvalRunner).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static int? Nullable(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{name} requires a value.");
        return args[index + 1];
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"{name} must be set.");

    private static string Truncate(string text) =>
        text.Length <= 120 ? text : text[..120] + "…";
}
