using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Ask;

/// <summary>
/// <c>ask-model</c>: contestant 8's small model (GLM-5.3-Flash on Bitdeer, low reasoning effort: the project's measurement
/// lineage) reads every turn and the turns before it, and names the doors to open, plus the turn rewritten to stand alone.
/// Recorded once and replayed by the model lane, free. The three-stage protocol: <c>--dry-run</c> prints one real request
/// and the token estimate and spends nothing; <c>--limit 1</c> asks one turn; then every turn not yet answered. The key
/// (<c>BITDEER_API_KEY</c>) is never printed.
/// </summary>
public sealed partial class ModelAsk(TextWriter output)
{
    public static string Instructions { get; } =
        "You route one user turn to the types of long-term memory an assistant should open before it replies.\n\nTypes:\n"
        + string.Join('\n', Doors.All.Select(d => $"- {Doors.Name(d)}: the reply needs {Doors.Meaning(d)}."))
        + """


        Choose every type the reply needs, any number of them. A question: the types holding its answer. A request (suggest,
        plan, write): the types that make the result fit the user. A statement (the user tells something): the types holding
        what it mentions, so the reply can connect or notice a change. A greeting, thanks or small talk with no content: none.
        General knowledge not about the user: none, unless the answer should fit the user.

        Also rewrite the turn so it stands alone, resolving pronouns and ellipsis from the earlier turns (keep the language).

        Answer with JSON only: {"doors": ["semantic", ...], "standalone": "..."}
        """;

    public static JsonArray Messages(MatrixItem item)
    {
        var lines = item.Prior.Select(p => $"{(p.Role == "user" ? "User" : "Assistant")}: {p.Text}").ToList();
        var turn = lines.Count > 0
            ? string.Join('\n', ["Earlier in this conversation:", .. lines, "", $"The turn to route:\nUser: {item.Text}"])
            : $"The turn to route:\nUser: {item.Text}";
        return [new JsonObject { ["role"] = "system", ["content"] = Instructions }, new JsonObject { ["role"] = "user", ["content"] = turn }];
    }

    public async Task<int> RunAsync(Matrix matrix, string outPath, bool dryRun, int? limit, int workers, CancellationToken cancellationToken = default)
    {
        var endpoint = (Environment.GetEnvironmentVariable("BITDEER_ENDPOINT") ?? "https://api-inference.bitdeer.ai/v1").TrimEnd('/');
        var model = Environment.GetEnvironmentVariable("BITDEER_MODEL") ?? "zai-org/GLM-5.3-Flash";
        var cache = File.Exists(outPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(outPath, cancellationToken).ConfigureAwait(false))!.AsObject()
            : new JsonObject { ["format"] = "model-lane/2", ["model"] = model, ["effort"] = "low", ["matrix"] = matrix.Sha256, ["answers"] = new JsonObject() };
        var answers = cache["answers"]!.AsObject();
        var todo = matrix.Items.Where(i => answers[i.Id]?["usage"] is null).ToList();
        var key = Environment.GetEnvironmentVariable("BITDEER_API_KEY");
        if (dryRun)
        {
            var sample = todo.FirstOrDefault(i => i.Prior.Count > 0) ?? todo.FirstOrDefault();
            if (sample is not null) output.WriteLine(Messages(sample).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var chars = todo.Sum(i => Messages(i).ToJsonString().Length);
            output.WriteLine($"ask-model: {todo.Count} turns to ask; about {(chars / 4).ToString("N0", CultureInfo.InvariantCulture)} input tokens in all, plus about {(todo.Count * 60).ToString("N0", CultureInfo.InvariantCulture)} out; key present: {key is not null}. Nothing was sent.");
            return 0;
        }
        if (key is null)
        {
            output.WriteLine("error: ask-model: BITDEER_API_KEY is not set");
            return 1;
        }
        if (limit is { } n) todo = [.. todo.Take(n)];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("agentmemory-routing-arena/1");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        output.WriteLine($"ask-model: {todo.Count} turns to {new Uri(endpoint).Host} ({model}, effort low)");
        var gate = new SemaphoreSlim(workers);
        var save = new object();
        var done = 0;
        await Task.WhenAll(todo.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var answer = await AskAsync(http, endpoint, model, item, cancellationToken).ConfigureAwait(false);
                lock (save) answers[item.Id] = answer;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                lock (save) answers[item.Id] = new JsonObject { ["error"] = $"{ex.GetType().Name}: {ex.Message}" };
            }
            finally
            {
                gate.Release();
                lock (save)
                {
                    if (++done % 25 == 0 || done == todo.Count)
                    {
                        File.WriteAllText(outPath, cache.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        output.WriteLine($"  {done}/{todo.Count}");
                    }
                }
            }
        })).ConfigureAwait(false);
        var answered = answers.Select(a => a.Value).Where(a => a?["usage"] is not null).ToList();
        var seconds = answered.Select(a => (double)a!["seconds"]!).Order().ToList();
        output.WriteLine($"ask-model: {answered.Count} answered, {answers.Count(a => a.Value?["error"] is not null)} failed; tokens in "
            + $"{answered.Sum(a => (int?)a!["usage"]!["prompt_tokens"] ?? 0):N0}, out {answered.Sum(a => (int?)a!["usage"]!["completion_tokens"] ?? 0):N0}; "
            + $"median {(seconds.Count > 0 ? seconds[seconds.Count / 2] : 0):0.##} s a turn");
        return answers.Any(a => a.Value?["error"] is not null) ? 2 : 0;
    }

    private static async Task<JsonObject> AskAsync(HttpClient http, string endpoint, string model, MatrixItem item, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = model, ["messages"] = Messages(item), ["temperature"] = 0.2, ["max_tokens"] = 400, ["reasoning_effort"] = "low",
        };
        for (var attempt = 0; ; attempt++)
        {
            var watch = Stopwatch.StartNew();
            using var response = await http.PostAsync($"{endpoint}/chat/completions",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is 429 or >= 500 && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
            var reply = JsonNode.Parse(body)!;
            var text = (string?)reply["choices"]![0]!["message"]!["content"] ?? "";
            var match = JsonObjectText().Match(text);
            var parsed = match.Success ? JsonNode.Parse(match.Value) : null;
            var doors = (parsed?["doors"] as JsonArray ?? [])
                .Select(d => (string?)d).Where(d => d is not null && Doors.All.Any(x => Doors.Name(x) == d)).Distinct().Order(StringComparer.Ordinal).ToList();
            return new JsonObject
            {
                ["doors"] = new JsonArray([.. doors.Select(d => (JsonNode)d!)]),
                ["standalone"] = (string?)parsed?["standalone"],
                ["raw"] = text,
                ["usage"] = reply["usage"]?.DeepClone() ?? new JsonObject(),
                ["seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 3),
                ["parsed"] = match.Success,
            };
        }
    }

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectText();
}
