using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpHost.Coding;

internal sealed record CodingApiResult(int StatusCode, object Body);

/// <summary>One strict request contract and operation dispatcher for HTTP and MCP.</summary>
internal sealed class CodingApi(CodingMemoryOperations memory, CodingWriteQueue queue)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public async Task<CodingApiResult> ExecuteAsync(CodingIdentity identity, string operation, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!identity.CanWrite && operation is "remember" or "forget") return Error(403, "insufficient_scope");
            if (arguments.ValueKind != JsonValueKind.Object) return Error(400, "invalid_input");
            switch (operation)
            {
                case "recall":
                    var recall = Read<RecallInput>(arguments);
                    var result = await memory.RecallAsync(identity, recall.Session, recall.Query, cancellationToken).ConfigureAwait(false);
                    return new(200, new { result.TotalItemsRetrieved, result.Truncated, result.EstimatedTokenCount,
                        context = McpMemoryProjection.Context(result.Context) });
                case "remember":
                    var job = queue.TryEnqueue(identity, Read<CodingRememberRequest>(arguments));
                    return job is null ? Error(503, "queue_unavailable") : new(202, job);
                case "forget":
                    var forget = Read<ForgetInput>(arguments);
                    var invalidated = await memory.ForgetAsync(identity, forget.Kind, forget.Id, cancellationToken).ConfigureAwait(false);
                    return new(200, new { invalidated });
                case "history":
                    var history = Read<HistoryInput>(arguments);
                    return new(200, await memory.HistoryAsync(identity, history.Id, cancellationToken).ConfigureAwait(false));
                case "profile":
                    Read<EmptyInput>(arguments);
                    return new(200, new { profile = await memory.ProfileAsync(identity, cancellationToken).ConfigureAwait(false) });
                case "write_status":
                    var status = Read<StatusInput>(arguments);
                    CodingInput.RequireText(status.JobId, 128, nameof(status.JobId));
                    var known = queue.Get(identity, status.JobId);
                    return known is null ? Error(404, "job_not_found") : new(200, known);
                default:
                    return Error(404, "unknown_operation");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CodingTurnConflictException) { return Error(409, "turn_conflict"); }
        catch (JsonException) { return Error(400, "invalid_input"); }
        catch (ArgumentException) { return Error(400, "invalid_input"); }
        catch (Exception) { return Error(503, "memory_unavailable"); }
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new JsonException();
    private static CodingApiResult Error(int status, string code) => new(status, new { error = code });

    private sealed record RecallInput { public required string Session { get; init; } public required string Query { get; init; } }
    private sealed record ForgetInput { public required MemoryHistoryKind Kind { get; init; } public required string Id { get; init; } }
    private sealed record HistoryInput { public string? Id { get; init; } }
    private sealed record StatusInput { public required string JobId { get; init; } }
    private sealed record EmptyInput;
}
