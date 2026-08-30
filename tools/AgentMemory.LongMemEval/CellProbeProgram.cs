using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Extracts an EXTERNAL corpus cell and reports whether amount subjects collide in the store.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this exists to settle.</b> Our probe found several distinct amounts under the bare
/// subject <c>Payment</c> and I scoped that as an extraction defect of ours. AgentEval showed it is
/// not: every amount-bearing sentence in their arithmetic corpus is
/// <c>"Payment logged against {job}: ${amount} for {item}."</c> — 248 of 248 — so the grammatical
/// subject really is the bare common noun, and our extractor reported what the sentence said.
/// </para>
/// <para>
/// Two explanations survive that, and they imply opposite work. Either their bare subject is the
/// cause (fixable by wording), or an amount is a <b>four-place fact</b> — payer, job, amount, line
/// item — that a triple store must reify or lose the join however it is worded, in which case no
/// rewording helps and the defect is ours in a different place. Cells B and C differ in exactly one
/// thing, the payment sentence's subject, so the pair separates them.
/// </para>
/// <para>
/// <b>No judge, no questions, no scoring.</b> This ingests and measures the store. That makes it
/// materially cheaper than an arm and keeps it incapable of producing an accuracy number that could
/// later be read as a result it was never designed to support.
/// </para>
/// </remarks>
internal static partial class CellProbeProgram
{
    internal static readonly string[] KnownOptions =
        ["--cell-probe", "--max-entries", "--skip-entries", "--dry-run", "--pair-with"];

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\s?\d|\d+\.\d{2}")]
    private static partial System.Text.RegularExpressions.Regex AmountPattern();

    public static async Task<int> RunAsync(string[] args)
    {
        var path = Value(args, "--cell-probe")
            ?? throw new ArgumentException("--cell-probe requires a path to a cell .json.");
        var maxEntries = int.TryParse(Value(args, "--max-entries"), out var n) ? n : int.MaxValue;
        path = Path.GetFullPath(path);

        var skip = int.TryParse(Value(args, "--skip-entries"), out var k) ? k : 0;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entries = document.RootElement.EnumerateArray().Skip(skip).Take(maxEntries).ToArray();

        // PRECONDITION, checked before a single LLM call. The first registered window was "the first
        // 12 entries", and payment sentences in this corpus start at entry 14 -- so the window held
        // ZERO of the phenomenon, the metric measured 0 of 0, and it printed "0.0%" as though that
        // were a reading. Two hours and ~2,200 calls bought a constant. A sampling rule that cannot
        // contain the thing being measured is a defect in the rule, and it is free to detect.
        var amountSentences = entries.Sum(entry =>
            entry.GetProperty("haystack_sessions").EnumerateArray()
                .Sum(session => session.EnumerateArray()
                    .Sum(turn => AmountPattern().Matches(
                        turn.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "").Count)));
        if (amountSentences == 0)
        {
            Console.Error.WriteLine(
                $"cell-probe: ABORT — the selected window (skip {skip}, take {maxEntries}) contains " +
                "NO amount-bearing sentences, so the amount-collision metric would divide by zero and " +
                "report 0.0% as if it were a measurement. Choose a window that contains the " +
                "phenomenon.");
            return 2;
        }
        Console.WriteLine($"cell-probe: window holds {amountSentences} amount-bearing sentence(s).");

        // DRY RUN. Everything the real run does EXCEPT the paid part: the corpus loads, the window
        // contains the phenomenon, the paired cell lines up entry-for-entry, the amount pattern
        // actually matches this corpus's sentences, and the store queries execute. Added because the
        // first cell run spent ~2,224 calls and two hours on a window that contained none of what it
        // measured -- a fact one free read of the corpus would have shown before ignition.
        if (args.Contains("--dry-run", StringComparer.Ordinal))
        {
            Console.WriteLine("cell-probe: DRY RUN — no LLM calls, no extraction.");
            var sessions = entries.Sum(e => e.GetProperty("haystack_sessions").GetArrayLength());
            Console.WriteLine(
                $"cell-probe:   entries {entries.Length} (skip {skip}), sessions {sessions}, " +
                $"amount sentences {amountSentences}");

            var sample = entries
                .SelectMany(e => e.GetProperty("haystack_sessions").EnumerateArray())
                .SelectMany(sess => sess.EnumerateArray())
                .Select(t => t.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")
                .FirstOrDefault(text => AmountPattern().IsMatch(text));
            Console.WriteLine($"cell-probe:   pattern matches, e.g. \"{Trim(sample ?? "(none)")}\"");

            if (Value(args, "--pair-with") is { } pairPath)
            {
                // The comparison is only controlled if entry i of one cell is entry i of the other.
                using var pair = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(pairPath)));
                var paired = pair.RootElement.EnumerateArray().Skip(skip).Take(maxEntries).ToArray();
                var lengthsMatch = entries.Length == paired.Length;
                var mismatch = lengthsMatch ? Enumerable
                    .Range(0, entries.Length)
                    .FirstOrDefault(i => entries[i].GetProperty("question_id").GetString()
                        != paired[i].GetProperty("question_id").GetString(), -1) : -1;
                Console.WriteLine(lengthsMatch && mismatch == -1
                    ? $"cell-probe:   PAIRING OK — {entries.Length} entries share question ids with " +
                      Path.GetFileName(pairPath)
                    : lengthsMatch
                        ? $"cell-probe:   PAIRING BROKEN at index {mismatch} — the cells are not aligned."
                        : $"cell-probe:   PAIRING BROKEN — window length mismatch " +
                          $"{entries.Length} vs {paired.Length}.");
                var pairedAmounts = paired.Sum(entry =>
                    entry.GetProperty("haystack_sessions").EnumerateArray()
                        .Sum(sess => sess.EnumerateArray()
                            .Sum(t => AmountPattern().Matches(
                                t.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "").Count)));
                Console.WriteLine($"cell-probe:   paired window holds {pairedAmounts} amount sentence(s).");
            }

            Console.WriteLine("cell-probe: DRY RUN COMPLETE — nothing spent.");
            return 0;
        }
        Console.WriteLine(
            $"cell-probe: {Path.GetFileName(path)} — {entries.Length} entr(ies), extraction only.");

        var endpoint = RequiredEnvironment("AZURE_OPENAI_ENDPOINT");
        var apiKey = RequiredEnvironment("AZURE_OPENAI_API_KEY");
        var deployment = RequiredEnvironment("AZURE_OPENAI_DEPLOYMENT");
        var embeddingDeployment = RequiredEnvironment("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
        var azure = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        using var extractionChat = new LongMemEvalChatCallMeter(
            new ProviderCompatibleExtractionChatClient(azure.GetChatClient(deployment).AsIChatClient()));
        var embeddings = azure.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator();
        var dimensions = await LongMemEvalRuntime
            .ProbeEmbeddingDimensionsAsync(embeddings).ConfigureAwait(false);

        await using var profile = await LongMemEvalMemoryProfile.StartAsync(
            embeddings, extractionChat, LongMemEvalMemoryMode.Structured, deployment, dimensions,
            Console.Out, CancellationToken.None).ConfigureAwait(false);

        var memory = profile.Services.GetRequiredService<IMemoryService>();
        var unit = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            // One owner per entry, mirroring the benchmark harness's isolation exactly: pooling them
            // would let two entries' payments collide for a reason that has nothing to do with the
            // cell under test.
            var owner = $"cell-owner-{i:D4}";
            var sessions = entries[i].GetProperty("haystack_sessions").EnumerateArray().ToArray();
            for (var s = 0; s < sessions.Length; s++)
            {
                var sessionId = $"cell-session-{i:D4}-{s:D3}";
                var messages = sessions[s].EnumerateArray()
                    .Select(turn => new Message
                    {
                        MessageId = Guid.NewGuid().ToString("n"),
                        ConversationId = sessionId,
                        SessionId = sessionId,
                        Role = turn.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user",
                        Content = turn.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                        TimestampUtc = DateTimeOffset.UtcNow,
                    })
                    .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                    .ToArray();
                if (messages.Length == 0) continue;

                await memory.AddMessagesAsync(messages, CancellationToken.None).ConfigureAwait(false);
                await memory.ExtractAndPersistAsync(
                    new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = owner },
                    CancellationToken.None).ConfigureAwait(false);
                if (++unit % 10 == 0) Console.WriteLine($"cell-probe: extraction units {unit}.");
            }
        }

        var probe = new Neo4jLongMemEvalGraphProbe(
            profile.Services.GetRequiredService<global::Neo4j.Driver.IDriver>());
        var objects = await probe.ReadFactObjectShapeAsync(CancellationToken.None).ConfigureAwait(false);
        var ambiguity = await probe.ReadSubjectAmbiguityAsync(CancellationToken.None).ConfigureAwait(false);
        var amountGroups = await probe.ReadAmountGroupCollisionAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Console.WriteLine(
            $"cell-probe: objects — {objects.AmountBearing} amount-bearing of {objects.Facts} facts.");
        // A share over an empty denominator is NOT MEASURED, and must never be rendered as 0.0% --
        // that is the constant column this project has now been bitten by three times, and once by
        // this very program.
        Console.WriteLine(amountGroups.AmountGroups == 0
            ? "cell-probe: AMOUNT-SUBJECT COLLISION — NOT MEASURED (zero amount-bearing groups in " +
              "the store; the metric has no denominator). This is not 0%."
            : $"cell-probe: AMOUNT-SUBJECT COLLISION — groups {amountGroups.CollidingGroups}/" +
              $"{amountGroups.AmountGroups} = {100.0 * amountGroups.CollidingGroups / amountGroups.AmountGroups:F1}%" +
              $"  |  AMOUNTS UNDER AN OVERLOADED SUBJECT {amountGroups.CollidingFacts}/" +
              $"{amountGroups.AmountFacts} = " +
              $"{(amountGroups.AmountFacts == 0 ? 0 : 100.0 * amountGroups.CollidingFacts / amountGroups.AmountFacts):F1}%" +
              "  (the second is the one comparable to their 48/48 per-payment calibration)");
        foreach (var sample in amountGroups.Samples) Console.WriteLine($"cell-probe:   {sample}");
        Console.WriteLine($"cell-probe: (subject-ambiguity groups overall: {ambiguity.Pairs.Count})");
        Console.WriteLine($"cell-probe: extraction LLM calls {extractionChat.Snapshot()}");
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 || index + 1 >= args.Length ? null : args[index + 1];
    }

    private static string Trim(string value) =>
        value.Length <= 90 ? value : value[..90] + "…";

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"{name} must be set.");
}
