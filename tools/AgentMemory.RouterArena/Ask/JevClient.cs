using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.RouterArena.Ask;

/// <summary>A yes/no question for the decision model: what to decide, and what yes and no mean.</summary>
public sealed record YesNo(string Instructions, string True, string False);

/// <summary>One answer: the model that answered, P(yes) per question, what it cost (when the provider says) and how long it took.</summary>
public sealed record JevReply(string Model, IReadOnlyDictionary<string, double> Yes, double? Cost, double Seconds);

/// <summary>
/// TypeSafe's System One protocol (the decision model JEV): a state and keyed yes/no questions in, P(yes) per question
/// out, in one round trip. The wire shape is the one AgentEval's <c>SystemOneDecisionClient</c> speaks. The key is sent
/// as a bearer token and never written anywhere; a failure says the host and the status, never the key.
/// </summary>
public sealed class JevClient(HttpClient http, Uri endpoint, string key, string model)
{
    public const string TypeSafeEndpoint = "https://api.typesafe.ai/v1/systemone";

    public string Model { get; } = model;

    /// <summary>The exact body a call sends: the dry run prints it.</summary>
    public string Render(object state, IReadOnlyDictionary<string, YesNo> questions)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["state"] = JsonSerializer.SerializeToNode(state, state.GetType(), Json),
            ["questions"] = new JsonObject(questions.Select(q => KeyValuePair.Create<string, JsonNode?>(q.Key, new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = q.Value.Instructions,
                ["criteria"] = new JsonObject { ["true"] = q.Value.True, ["false"] = q.Value.False },
            }))),
        };
        return body.ToJsonString();
    }

    /// <summary>Asks, retrying a rate limit, an overload or a provider failure up to four times with a growing pause.</summary>
    public async Task<JevReply> AskAsync(object state, IReadOnlyDictionary<string, YesNo> questions, CancellationToken cancellationToken = default)
    {
        var json = Render(state, questions);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (key.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var watch = Stopwatch.StartNew();
            HttpResponseMessage? response = null;
            string body;
            try
            {
                response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested && attempt < 4)
            {
                response?.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 429 or 529 or >= 500 && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 << attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException($"{endpoint.Host} answered HTTP {status}: {Excerpt(body)}");
                return Parse(body, questions.Keys, watch.Elapsed.TotalSeconds);
            }
        }
    }

    internal static JevReply Parse(string body, IEnumerable<string> asked, double seconds)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var answers = root.GetProperty("answers");
        var yes = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var id in asked)
            yes[id] = answers.GetProperty(id).GetProperty("noul").GetDouble();
        double? cost = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDouble()
            : null;
        return new JevReply(root.TryGetProperty("model", out var model) ? model.GetString() ?? "?" : "?", yes, cost, Math.Round(seconds, 3));
    }

    private static string Excerpt(string body) => body.Length <= 300 ? body : body[..300] + "…";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
