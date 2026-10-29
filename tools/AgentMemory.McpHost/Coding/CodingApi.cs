using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;
#pragma warning disable AMEXT001 // CMP-1 anchors come from the Extensibility preview.
using AgentMemory.Extensibility.Context;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpHost.Coding;

internal sealed record CodingApiResult(int StatusCode, object Body);

/// <summary>One strict request contract and operation dispatcher for HTTP and MCP.</summary>
internal sealed class CodingApi(CodingMemoryOperations memory, CodingWriteQueue queue, CodingRecallReceipts? receipts = null,
    CodingEvidence? evidence = null, CodingReview? review = null)
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
            if (!identity.CanWrite && operation is "remember" or "forget" or "review_decide") return Error(403, "insufficient_scope");
            if (receipts is not null && !identity.CanWrite && operation == "recall_feedback") return Error(403, "insufficient_scope");
            if (arguments.ValueKind != JsonValueKind.Object) return Error(400, "invalid_input");
            switch (operation)
            {
                case "recall_preview" when evidence is not null:
                    var preview = Read<RecallInput>(arguments);
                    return new(200, await evidence.PreviewAsync(identity, preview.Session, preview.Query, cancellationToken).ConfigureAwait(false));
                case "recall_expand" when evidence is not null:
                    var expand = Read<EvidenceInput>(arguments);
                    var expanded = await evidence.ExpandAsync(identity, expand.Session, expand.PreviewId, expand.Items, cancellationToken).ConfigureAwait(false);
                    return expanded is null ? Error(404, "preview_not_found") : new(200, expanded);
                case "recall":
                    var recall = Read<RecallInput>(arguments);
                    var started = receipts is null ? 0L : Stopwatch.GetTimestamp();
                    var recalled = await memory.RecallWithModulesAsync(identity, recall.Session, recall.Query, CodingAnchors.From(recall.Anchors),
                        cancellationToken).ConfigureAwait(false);
                    var result = recalled.Result;
                    if (receipts is not null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var receipt = receipts.Record(identity, recall.Session, result, Stopwatch.GetElapsedTime(started));
                        return recalled.Modules is { } withReceipt
                            ? new(200, new { result.TotalItemsRetrieved, result.Truncated, result.EstimatedTokenCount,
                                context = McpMemoryProjection.Context(result.Context), modules = withReceipt, recallId = receipt.RecallId })
                            : new(200, new { result.TotalItemsRetrieved, result.Truncated, result.EstimatedTokenCount,
                                context = McpMemoryProjection.Context(result.Context), recallId = receipt.RecallId });
                    }
                    // Without modules the response keeps its exact former shape.
                    return recalled.Modules is { } items
                        ? new(200, new { result.TotalItemsRetrieved, result.Truncated, result.EstimatedTokenCount,
                            context = McpMemoryProjection.Context(result.Context), modules = items })
                        : new(200, new { result.TotalItemsRetrieved, result.Truncated, result.EstimatedTokenCount,
                            context = McpMemoryProjection.Context(result.Context) });
                case "governs":
                    // CMP-1: recall whose question is the anchors themselves, so the gate weighs what governs them.
                    var governs = Read<GovernsInput>(arguments);
                    // What the walk takes, not what was given (review 5: blank and duplicate anchors reached the question and the echo).
                    var anchors = ContextAnchors.Taken(CodingAnchors.From(governs.Anchors));
                    if (anchors.Count == 0) return Error(400, "invalid_input");
                    var governed = await memory.RecallWithModulesAsync(identity, governs.Session, CodingAnchors.Question(anchors), anchors,
                        cancellationToken).ConfigureAwait(false);
                    return new(200, new { anchors = anchors.Select(a => new { kind = a.Kind == ContextAnchorKind.Path ? "path" : "symbol", value = a.Value }),
                        modules = governed.Modules ?? [], context = McpMemoryProjection.Context(governed.Result.Context) });
                case "recall_status" when receipts is not null:
                    var lookup = Read<RecallStatusInput>(arguments);
                    var observation = receipts.Get(identity, lookup.Session, lookup.RecallId);
                    return observation is null ? Error(404, "recall_not_found") : new(200, observation);
                case "recall_feedback" when receipts is not null:
                    var feedback = Read<RecallFeedbackInput>(arguments);
                    var updated = receipts.Feedback(identity, feedback.Session, feedback.RecallId, feedback.Label);
                    return updated is null ? Error(404, "recall_not_found") : new(200, updated);
                case "review_list" when review is not null:
                    var list = Read<ReviewListInput>(arguments);
                    return new(200, review.List(list.Pack, list.State is { } s ? Enum.Parse<Extensibility.Packs.ReviewState>(s, ignoreCase: true) : null));
                case "review_decide" when review is not null:
                    var decide = Read<ReviewDecideInput>(arguments);
                    if (decide.Decision is not ("accept" or "reject") || decide.Keys.Count is 0 or > 50) return Error(400, "invalid_input");
                    var decided = review.Decide(identity, decide.Pack, decide.Keys, decide.Decision == "accept", decide.Edit, decide.Note);
                    return decided is null ? Error(404, "review_set_not_found") : new(200, decided);
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
        catch (CodingEvidenceDeadlineException) { return Error(504, "evidence_timeout"); }
        catch (CodingTurnConflictException) { return Error(409, "turn_conflict"); }
        catch (JsonException) { return Error(400, "invalid_input"); }
        catch (ArgumentException) { return Error(400, "invalid_input"); }
        catch (Exception) { return Error(503, "memory_unavailable"); }
    }

    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new JsonException();
    private static CodingApiResult Error(int status, string code) => new(status, new { error = code });

    private sealed record RecallInput
    {
        public required string Session { get; init; }
        public required string Query { get; init; }
        public IReadOnlyList<CodingAnchorInput>? Anchors { get; init; }
    }
    private sealed record GovernsInput { public required string Session { get; init; } public required IReadOnlyList<CodingAnchorInput> Anchors { get; init; } }
    private sealed record ForgetInput { public required MemoryHistoryKind Kind { get; init; } public required string Id { get; init; } }
    private sealed record HistoryInput { public string? Id { get; init; } }
    private sealed record StatusInput { public required string JobId { get; init; } }
    private sealed record RecallStatusInput { public required string Session { get; init; } public string? RecallId { get; init; } }
    private sealed record RecallFeedbackInput
    {
        public required string Session { get; init; }
        public required string RecallId { get; init; }
        public required CodingRecallFeedback Label { get; init; }
    }
    private sealed record EvidenceInput
    {
        public required string Session { get; init; }
        public required string PreviewId { get; init; }
        public required IReadOnlyList<CodingEvidenceSelection> Items { get; init; }
    }
    private sealed record EmptyInput;
    private sealed record ReviewListInput { public string? Pack { get; init; } public string? State { get; init; } }
    private sealed record ReviewDecideInput
    {
        public required string Pack { get; init; }
        public required IReadOnlyList<string> Keys { get; init; }
        public required string Decision { get; init; }
        public Dictionary<string, JsonElement>? Edit { get; init; }
        public string? Note { get; init; }
    }
}
