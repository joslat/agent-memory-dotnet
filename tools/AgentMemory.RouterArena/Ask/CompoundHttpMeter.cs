using System.Text.Json;

namespace AgentMemory.RouterArena.Ask;

/// <summary>Counts physical attempts without logging request bodies, credentials or provider error text.</summary>
internal sealed class CompoundHttpMeter(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly object _sync = new();
    private long _attempts, _failed, _pending, _usageReports, _input, _output;

    public CompoundRequestCounts Snapshot()
    {
        lock (_sync)
            return new(_attempts, _failed, _pending, _usageReports,
                _usageReports == 0 ? null : _input, _usageReports == 0 ? null : _output);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_sync) { _attempts++; _pending++; }
        HttpResponseMessage? response = null;
        var failureCounted = false;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                lock (_sync) _failed++;
                failureCounted = true;
            }
            // ReadAsStringAsync buffers the content; the real protocol client reads the same response afterwards.
            // Never retain or print the body. Missing/incomplete usage is unknown, not zero tokens.
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                    Tokens(usage, "prompt_tokens", "input_tokens") is { } input &&
                    Tokens(usage, "completion_tokens", "output_tokens") is { } output)
                {
                    lock (_sync) { _usageReports++; _input += input; _output += output; }
                }
            }
            catch (JsonException) { }
            return response;
        }
        catch
        {
            response?.Dispose();
            if (!failureCounted) { lock (_sync) _failed++; }
            throw;
        }
        finally
        {
            lock (_sync) _pending--;
        }
    }

    private static long? Tokens(JsonElement usage, string first, string second) =>
        (usage.TryGetProperty(first, out var value) || usage.TryGetProperty(second, out value)) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? count : null;

    public static CompoundRequestCounts Sum(CompoundRequestCounts first, CompoundRequestCounts second) => new(
        first.Attempts + second.Attempts, first.Failed + second.Failed, first.Pending + second.Pending,
        first.TokenReportedResponses + second.TokenReportedResponses,
        first.InputTokens is null && second.InputTokens is null ? null : (first.InputTokens ?? 0) + (second.InputTokens ?? 0),
        first.OutputTokens is null && second.OutputTokens is null ? null : (first.OutputTokens ?? 0) + (second.OutputTokens ?? 0))
    {
        JudgeFailures = first.JudgeFailures + second.JudgeFailures,
    };
}
