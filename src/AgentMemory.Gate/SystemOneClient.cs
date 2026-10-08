using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Gate;

/// <summary>A yes/no question for a decision model: what to decide, and what yes and no mean.</summary>
internal sealed record YesNo(string Instructions, string True, string False);

/// <summary>
/// The System One protocol (TypeSafe's decision model JEV, or a local server speaking it): a state and keyed yes/no
/// questions in, P(yes) per question out. The key is read from the environment and never logged.
/// </summary>
internal sealed class SystemOneClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Health> _health = new(StringComparer.Ordinal);

    public SystemOneClient(HttpClient http, TimeProvider? time = null, ILogger? logger = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>A judge's run of failures, and when it is left out until (MemoryGateOptions.JudgeFailuresBeforeCooldown).</summary>
    private sealed class Health
    {
        public int Failures;
        public DateTimeOffset? OutUntil;
        public bool Probing;
    }

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

    /// <summary>
    /// P(yes) per question; retries a rate limit or a server failure once. A judge that failed
    /// <see cref="MemoryGateOptions.JudgeFailuresBeforeCooldown"/> calls in a row is not called during its cool-down: the call
    /// throws <see cref="JudgeCoolingDownException"/> at once.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> AskAsync(
        SystemOneEndpoint endpoint, object state, IReadOnlyDictionary<string, YesNo> questions, MemoryGateOptions options,
        CancellationToken cancellationToken)
    {
        var key = $"{endpoint.Name}|{endpoint.Endpoint}";
        var limit = options.JudgeFailuresBeforeCooldown;
        if (limit > 0)
            Enter(endpoint.Name, key);
        try
        {
            var answers = await SendAsync(endpoint, state, questions, cancellationToken).ConfigureAwait(false);
            if (limit > 0)
                Succeeded(endpoint.Name, key);
            return answers;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (limit > 0)
                Released(key);
            throw;
        }
        catch (Exception) when (limit > 0)
        {
            Failed(endpoint.Name, key, limit, options.JudgeCooldown);
            throw;
        }
    }

    /// <summary>Until when the judge is left out, if it is now (for the tier line); null when it is asked.</summary>
    public DateTimeOffset? OutUntil(SystemOneEndpoint endpoint)
    {
        lock (_health)
        {
            return _health.TryGetValue($"{endpoint.Name}|{endpoint.Endpoint}", out var health) && health.OutUntil is { } until
                && until > _time.GetUtcNow() ? until : null;
        }
    }

    /// <summary>Lets the call through, or throws when the judge is cooling down (one probe goes through once it is over).</summary>
    private void Enter(string name, string key)
    {
        DateTimeOffset until;
        lock (_health)
        {
            if (!_health.TryGetValue(key, out var health) || health.OutUntil is null)
                return;
            if (!health.Probing && _time.GetUtcNow() >= health.OutUntil)
            {
                health.Probing = true;
                return;
            }
            until = health.OutUntil.Value;
        }
        Activity.Current?.AddEvent(new ActivityEvent("memory.judge.skipped", tags: new ActivityTagsCollection { ["memory.judge"] = name }));
        throw new JudgeCoolingDownException(name, until);
    }

    private void Succeeded(string name, string key)
    {
        bool back;
        lock (_health)
        {
            back = _health.Remove(key, out var health) && health.OutUntil is not null;
        }
        if (back)
            _logger.LogInformation("The judge '{Judge}' answered again; it is asked on every call from now on.", name);
    }

    private void Released(string key)
    {
        lock (_health)
        {
            if (_health.TryGetValue(key, out var health))
                health.Probing = false;
        }
    }

    private void Failed(string name, string key, int limit, TimeSpan cooldown)
    {
        DateTimeOffset? until = null;
        int failures;
        lock (_health)
        {
            if (!_health.TryGetValue(key, out var health))
                _health[key] = health = new Health();
            failures = ++health.Failures;
            health.Probing = false;
            if (failures >= limit)
                until = health.OutUntil = _time.GetUtcNow() + cooldown;
        }
        if (until is { } u)
        {
            _logger.LogWarning(
                "The judge '{Judge}' failed {Failures} calls in a row; it is left out until {Until:HH:mm:ss} UTC, then asked once.",
                name, failures, u);
            Activity.Current?.AddEvent(new ActivityEvent("memory.judge.cooldown", tags: new ActivityTagsCollection
            {
                ["memory.judge"] = name, ["memory.judge.failures"] = failures,
            }));
        }
    }

    private async Task<IReadOnlyDictionary<string, double>> SendAsync(
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
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// A judge left out for its cool-down after failing <see cref="MemoryGateOptions.JudgeFailuresBeforeCooldown"/> calls in a
/// row: it was not called.
/// </summary>
internal sealed class JudgeCoolingDownException(string judge, DateTimeOffset until)
    : Exception($"the judge '{judge}' is left out until {until:HH:mm:ss} UTC after repeated failures")
{
    public string Judge { get; } = judge;
    public DateTimeOffset Until { get; } = until;
}
