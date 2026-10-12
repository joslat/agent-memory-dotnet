using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Abstractions.Options;
using Microsoft.Extensions.Options;
using System.Text.Json;

#pragma warning disable AMWRITE001 // Coding mode explicitly requires the store-aware writer.

namespace AgentMemory.McpHost.Coding;

internal sealed record CodingWriteResult
{
    public required string SourceMessageId { get; init; }
    public bool PriorAttemptUnknown { get; init; }
    public IngestionStatus? Status { get; init; }
}

/// <summary>Writer-only ingestion, shared by the coding transports through the background worker.</summary>
internal interface ICodingWriter
{
    Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class CodingWriter(CodingOperationScope scopes, CodingContentPolicy content) : ICodingWriter
{
    public Task<CodingWriteResult> WriteAsync(CodingIdentity identity, CodingRememberRequest request,
        CancellationToken cancellationToken = default)
    {
        content.Check(request);
        return scopes.RunAsync(identity, async (services, ct) =>
        {
            var session = CodingInput.SessionId(identity, request.Session);
            var sourceId = CodingInput.MessageId(session, request.TurnId, "user");
            var hash = request.PayloadHash();
            var prior = await services.GetRequiredService<IMessageRepository>()
                .GetByIdAsync(sourceId, ct).ConfigureAwait(false);
            if (prior is not null)
            {
                EnsureSameTurn(prior, session, sourceId, request.UserText, hash);
                // A source record proves an attempt started, not that extraction committed successfully.
                return new CodingWriteResult { SourceMessageId = sourceId, PriorAttemptUnknown = true };
            }

            if (services.GetRequiredService<IOptions<ExtractionOptions>>().Value.FallBackToExtractorsWhenWriterFails)
                throw new InvalidOperationException("Coding ingestion requires extractor fallback to be disabled.");
            if (!services.GetServices<IMemoryWriter>().Any(w => w.IsEnabled))
                throw new InvalidOperationException("Coding ingestion requires an enabled memory writer.");
            var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
            var context = BoundedContext(await shortTerm.GetRecentMessagesAsync(session, 6, ct).ConfigureAwait(false));
            foreach (var message in context) content.CheckText(message.Content);
            var metadata = new Dictionary<string, object>
            {
                ["coding_commit"] = request.Commit, ["coding_source"] = request.Source,
                ["coding_payload_sha256"] = hash,
            };
            await shortTerm.AddConversationAsync(session, session, identity.OwnerId, cancellationToken: ct).ConfigureAwait(false);
            var ingestion = services.GetRequiredService<IMemoryIngestion>();
            var user = await ingestion.AddMessageWithIdAsync(session, session, "user", request.UserText, sourceId,
                metadata, ct).ConfigureAwait(false);
            EnsureSameTurn(user, session, sourceId, request.UserText, hash);
            var targets = new List<Message> { user };
            if (request.AssistantText is { } reply)
            {
                var assistantId = CodingInput.MessageId(session, request.TurnId, "assistant");
                var assistant = await ingestion.AddMessageWithIdAsync(session, session, "assistant", reply,
                    assistantId, metadata, ct).ConfigureAwait(false);
                EnsureSameTurn(assistant, session, assistantId, reply, hash, "assistant");
                targets.Add(assistant);
            }
            var result = await ingestion.ExtractAndPersistAsync(new ExtractionRequest
            {
                UserId = identity.OwnerId, SessionId = session, Messages = targets, ContextMessages = context,
                InputMode = ExtractionInputMode.ConversationTurn, TrustLevel = MemoryTrustLevel.Untrusted,
            }, ct).ConfigureAwait(false);
            return new CodingWriteResult { SourceMessageId = sourceId, Status = result.Status };
        }, cancellationToken);
    }

    private static void EnsureSameTurn(Message stored, string session, string id, string text, string hash, string role = "user")
    {
        stored.Metadata.TryGetValue("coding_payload_sha256", out var value);
        var recordedHash = value is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : value as string;
        if (stored.MessageId != id || stored.SessionId != session || stored.ConversationId != session ||
            stored.Role != role || stored.Content != text || recordedHash != hash)
            throw new InvalidOperationException("Turn ID already belongs to different content or provenance.");
    }

    private static IReadOnlyList<Message> BoundedContext(IReadOnlyList<Message> recent)
    {
        var chosen = new List<Message>();
        var length = 0;
        foreach (var message in recent.Take(6))
        {
            if (message.InvalidatedAtUtc is not null || message.Role is not ("user" or "assistant")) continue;
            if (length + message.Content.Length > 8000) break;
            length += message.Content.Length;
            chosen.Add(message);
        }
        chosen.Reverse();
        return chosen;
    }
}
