using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentMemory.Gate;

/// <summary>A yes/no question for a decision model: what to decide, and what yes and no mean.</summary>
internal sealed record YesNo(string Instructions, string True, string False);

/// <summary>
/// The System One protocol (TypeSafe's decision model JEV, or a local server speaking it): a state and keyed yes/no
/// questions in, P(yes) per question out. The key is read from the environment and never logged.
/// </summary>
internal sealed class SystemOneClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The request body, exactly as it is sent.</summary>
    public static string Render(string model, object state, IReadOnlyDictionary<string, YesNo> questions) =>
        new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(state, state.GetType(), Json),
            ["questions"] = new JsonObject(questions.Select(q => KeyValuePair.Create<string, JsonNode?>(q.Key, new JsonObject
            {
                ["type"] = "noul",
                ["instructions"] = q.Value.Instructions,
                ["criteria"] = new JsonObject { ["true"] = q.Value.True, ["false"] = q.Value.False },
            }))),
        }.ToJsonString();

    /// <summary>P(yes) per question; retries a rate limit or a server failure once.</summary>
    public async Task<IReadOnlyDictionary<string, double>> AskAsync(
        SystemOneEndpoint endpoint, object state, IReadOnlyDictionary<string, YesNo> questions, CancellationToken cancellationToken)
    {
        var body = Render(endpoint.Model, state, questions);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (endpoint.KeyVariable is { } variable && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } key)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(text, questions.Keys);
        }
    }

    internal static IReadOnlyDictionary<string, double> Parse(string body, IEnumerable<string> asked)
    {
        using var document = JsonDocument.Parse(body);
        var answers = document.RootElement.GetProperty("answers");
        return asked.ToDictionary(id => id, id => answers.GetProperty(id).GetProperty("noul").GetDouble(), StringComparer.Ordinal);
    }
}
