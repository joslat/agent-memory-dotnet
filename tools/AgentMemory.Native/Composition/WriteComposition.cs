using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Extraction.Llm;
using AgentMemory.Native.Chat;
using AgentMemory.Native.Embeddings;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Native.Composition;

/// <summary>One thing the owner asked to have remembered, in their own words, as the speech server heard it.</summary>
internal sealed record RememberInput
{
    public required string Owner { get; init; }
    public required string SessionId { get; init; }
    public required string ConversationId { get; init; }

    /// <summary>Stable: the same words resubmitted under the same id are the same message, never a second one.</summary>
    public required string MessageId { get; init; }

    public required string Text { get; init; }

    /// <summary>When the words were said (not when they are written): relative dates are read against it.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Learning profile: apply the write once by this id (ExtractionRequest.OperationId): every effect and a receipt in one
    /// store transaction, and nothing runs again once the receipt exists. Null: the explicit-write profile's behaviour.
    /// </summary>
    public string? OperationId { get; init; }
}

/// <summary>What one remember wrote: the stored message and what the writer extracted from it.</summary>
internal sealed record RememberOutcome(Message Message, ExtractionResult Extraction);

/// <summary>A fact or preference the store links to a remembered message, as the store shows it.</summary>
internal sealed record WrittenItem(string Id, string Kind, string? Owner, bool Live, long Dimensions);

/// <summary>What the store shows a remembered message produced: its facts and preferences, and what they closed.</summary>
internal sealed record WrittenEvidence(IReadOnlyList<WrittenItem> Items, IReadOnlyList<string> Closed)
{
    /// <summary>Anything recall reads came from the message.</summary>
    public bool Any => Items.Count > 0 || Closed.Count > 0;

    /// <summary>The first reason the items cannot be called searchable by the owner, or null when every one is.</summary>
    public string? Problem(string owner, int dimensions) =>
        Items.FirstOrDefault(i => i.Owner != owner) is { } foreign ? $"{foreign.Kind} {foreign.Id} is not the owner's"
        : Items.FirstOrDefault(i => !i.Live) is { } closed ? $"{closed.Kind} {closed.Id} is closed"
        : Items.FirstOrDefault(i => i.Dimensions != dimensions) is { } bare
            ? $"{bare.Kind} {bare.Id} has {bare.Dimensions} embedding dimensions, not {dimensions}"
        : null;
}

/// <summary>
/// The composition root of the explicit-write (R1) native profile: the read-only profile's narrowing (no working
/// memory, strict owner isolation, fail-fast store timeouts) plus the write path, with the native core's own
/// OpenAI-compatible chat and embedding clients (no SDK). Decisions, each from the design (P07) or the assessment (A15):
/// <list type="bullet">
/// <item>The store-aware writer, in <see cref="ExtractionInputMode.ConversationTurn"/>: one model call per remembered
/// turn that sees the owner's most relevant stored memories, so a change replaces what it changes instead of piling up.</item>
/// <item>No silent step down to the extractors when the writer's call fails: the turn stores nothing and says so
/// (<see cref="ExtractionOptions.FallBackToExtractorsWhenWriterFails"/> false), so a "saved" is never a different
/// extraction than the one asked for.</item>
/// <item>Evidence-based entity identity off (<see cref="IdentityResolutionOptions.Enabled"/> false): explicit remember
/// needs facts and preferences, and the identity payload feeds stored hashes that the native build does not serialize
/// yet (A15). The profile has no setting that turns it on.</item>
/// <item>No context turns, for extraction or identity: an empty supplied context stays empty, so nothing but the
/// remembered words is extracted.</item>
/// <item>A stored memory the writer names as replaced is closed only when the update judge (the same chat model)
/// confirms it, as in the library's Recommended preset: a correction ends the old value, an unconfirmed one leaves it.</item>
/// <item>Dates the facts' own words state become date fields, read against when the words were said.</item>
/// <item>The model's raw reply is never logged: it holds what the user said.</item>
/// </list>
/// </summary>
internal static class WriteComposition
{
    public static ServiceProvider Build(MemorySettings settings, ChatEndpoint chat, EmbeddingEndpoint embedding)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(embedding);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(_ => new OpenAiCompatibleChatClient(chat));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
            _ => new OpenAiCompatibleEmbeddingGenerator(embedding, settings.EmbeddingDimensions));
        services.AddAgentMemoryCore(options =>
        {
            options.WorkingMemory.Enabled = false;
            options.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant;
            options.Extraction.FallBackToExtractorsWhenWriterFails = false;
            options.Extraction.ExtractionContextTurns = 0;
        });
        services.Configure<IdentityResolutionOptions>(identity =>
        {
            identity.Enabled = false;
            identity.ContextTurns = 0;
        });
        services.AddNeo4jAgentMemory(neo4j =>
        {
            neo4j.Uri = settings.Uri;
            neo4j.Username = settings.Username;
            neo4j.Password = settings.Password;
            neo4j.Database = settings.Database;
            neo4j.EmbeddingDimensions = settings.EmbeddingDimensions;
            neo4j.TransactionTimeout = settings.TransactionTimeout;
            neo4j.ConnectionAcquisitionTimeout = settings.ConnectionAcquisitionTimeout;
            neo4j.ConnectionTimeout = settings.ConnectionTimeout;
            neo4j.MaxTransactionRetryTime = settings.MaxTransactionRetryTime;
        });
        services.AddLlmExtraction(llm =>
        {
            llm.UseMemoryWriter = true;
            // A stored memory the writer names as replaced closes only when a judge confirms it; without one, "I work in
            // Basel now" would leave Zurich live beside it. The library's Recommended writer setting, as measured.
            llm.ChatModelUpdateJudge = true;
            llm.MemoryWriterEventIdentities = false;
            llm.CaptureIdentityAliases = false;
            llm.ModelId = chat.Model;
            llm.Temperature = 0f;
            llm.DatesFromText = true;
            llm.LogRawResponseOnFailure = false;
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>
    /// Writes the owner's words as a message with its own id and time, in the owner's conversation, then lets the
    /// store-aware writer turn them into facts and preferences. Everything runs inside the owner's scope, so strict
    /// isolation stamps and checks the owner on every node.
    /// </summary>
    public static async Task<RememberOutcome> RememberAsync(
        ServiceProvider provider, RememberInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(input);

        var scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            using (services.GetRequiredService<IWritableMemoryOwnerContext>().BeginOwnerScope(input.Owner))
            {
                var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
                await shortTerm.AddConversationAsync(input.ConversationId, input.SessionId, input.Owner,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var message = await shortTerm.AddMessageAsync(new Message
                {
                    MessageId = input.MessageId,
                    ConversationId = input.ConversationId,
                    SessionId = input.SessionId,
                    Role = "user",
                    Content = input.Text,
                    TimestampUtc = input.OccurredAt,
                }, cancellationToken).ConfigureAwait(false);

                var extraction = await services.GetRequiredService<IMemoryService>().ExtractAndPersistAsync(new ExtractionRequest
                {
                    InputMode = ExtractionInputMode.ConversationTurn,
                    Messages = [message],
                    SessionId = input.SessionId,
                    // The owner of extracted memories is explicit on the request; the ambient scope does not set it.
                    UserId = input.Owner,
                    OperationId = input.OperationId,
                }, cancellationToken).ConfigureAwait(false);
                return new RememberOutcome(message, extraction);
            }
        }
    }

    /// <summary>
    /// Closes (invalidates) each id that is one of the owner's facts or preferences. Returns the ids closed now and the
    /// ids that are no fact or preference of this owner. A closed memory stays as history for as-of reads; recall of the
    /// present no longer returns it. This is "stop using", never "delete".
    /// </summary>
    public static async Task<(IReadOnlyList<string> Closed, IReadOnlyList<string> NotFound)> ForgetAsync(
        ServiceProvider provider, string owner, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var closed = new List<string>();
        var missing = new List<string>();
        var scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            using (services.GetRequiredService<IWritableMemoryOwnerContext>().BeginOwnerScope(owner))
            {
                var longTerm = services.GetRequiredService<ILongTermMemoryService>();
                var own = Abstractions.Options.MemoryScope.For(owner, includeShared: false);
                foreach (var id in ids)
                {
                    if (await longTerm.InvalidateFactAsync(id, own, cancellationToken).ConfigureAwait(false)
                        || await longTerm.InvalidatePreferenceAsync(id, own, cancellationToken).ConfigureAwait(false))
                        closed.Add(id);
                    else
                        missing.Add(id);
                }
            }
        }
        return (closed, missing);
    }

    private const string StateQuery = """
        MATCH (n) WHERE n.id IN $ids AND (n:Fact OR n:Preference)
        RETURN n.id AS id, CASE WHEN n:Fact THEN 'fact' ELSE 'preference' END AS kind, n.owner_id AS owner,
               n.invalidated_at IS NULL AS live, CASE WHEN n.embedding IS NULL THEN 0 ELSE size(n.embedding) END AS dims
        """;

    /// <summary>The store's view of these fact or preference ids (ids that are neither are absent from the result).</summary>
    public static Task<IReadOnlyList<WrittenItem>> ItemsAsync(
        ServiceProvider provider, MemorySettings settings, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadAsync<IReadOnlyList<WrittenItem>>(provider, settings, async transaction =>
        {
            var items = new List<WrittenItem>();
            var cursor = await transaction.RunAsync(StateQuery, new Dictionary<string, object> { ["ids"] = ids.ToList() })
                .ConfigureAwait(false);
            while (await cursor.FetchAsync().ConfigureAwait(false))
            {
                var record = cursor.Current;
                items.Add(new WrittenItem((string)record["id"], (string)record["kind"], record["owner"] as string,
                    record["live"] is true, record["dims"] is long dims ? dims : 0));
            }
            return items;
        });

    /// <summary>Deletes every node of the owner, with their conversations and messages; returns how many went.</summary>
    public static async Task<long> EraseOwnerAsync(ServiceProvider provider, string owner, CancellationToken cancellationToken)
    {
        var scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var erasure = await scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>()
                .EraseAsync(owner, cancellationToken).ConfigureAwait(false);
            return erasure.Total;
        }
    }

    private const string RemainingQuery = """
        CALL { MATCH (n) WHERE n.owner_id = $owner RETURN count(n) AS owned }
        CALL { MATCH (c:Conversation {user_id: $owner}) RETURN count(c) AS conversations }
        CALL { MATCH (m:Message) WHERE m.id IN $messageIds RETURN count(m) AS messages }
        RETURN owned + conversations + messages AS remaining
        """;

    /// <summary>What is left of the owner after an erase: owned nodes, conversations and the remembered messages.</summary>
    public static Task<long> RemainingAsync(ServiceProvider provider, MemorySettings settings, string owner,
        IReadOnlyList<string> messageIds, CancellationToken cancellationToken) =>
        ReadAsync(provider, settings, async transaction =>
        {
            var cursor = await transaction.RunAsync(RemainingQuery, new Dictionary<string, object>
            {
                ["owner"] = owner, ["messageIds"] = messageIds.ToList(),
            }).ConfigureAwait(false);
            return await cursor.FetchAsync().ConfigureAwait(false) && cursor.Current["remaining"] is long remaining ? remaining : -1;
        });

    private static async Task<T> ReadAsync<T>(ServiceProvider provider, MemorySettings settings,
        Func<global::Neo4j.Driver.IAsyncQueryRunner, Task<T>> work)
    {
        var driver = provider.GetRequiredService<global::Neo4j.Driver.IDriver>();
        var session = driver.AsyncSession(o => o.WithDatabase(settings.Database)
            .WithDefaultAccessMode(global::Neo4j.Driver.AccessMode.Read));
        await using (session.ConfigureAwait(false))
        {
            return await session.ExecuteReadAsync(transaction => work(transaction)).ConfigureAwait(false);
        }
    }

    private const string ItemsQuery = """
        MATCH (:Message {id: $messageId})<-[:EXTRACTED_FROM]-(n)
        WHERE n:Fact OR n:Preference
        RETURN DISTINCT n.id AS id, CASE WHEN n:Fact THEN 'fact' ELSE 'preference' END AS kind, n.owner_id AS owner,
               n.invalidated_at IS NULL AS live, CASE WHEN n.embedding IS NULL THEN 0 ELSE size(n.embedding) END AS dims
        """;

    private const string ClosedQuery = """
        MATCH (:Message {id: $messageId})<-[:EXTRACTED_FROM]-(n)<-[:SUPERSEDED_BY]-(old)
        WHERE (n:Fact OR n:Preference) AND (old:Fact OR old:Preference)
        RETURN DISTINCT old.id AS id
        """;

    /// <summary>
    /// Reads, in one read transaction, what the store links to a remembered message: the facts and preferences extracted
    /// from it (with owner, liveness and embedding size) and the memories they closed. This is the evidence a receipt
    /// rests on, independent of what the writer reported.
    /// </summary>
    public static async Task<WrittenEvidence> EvidenceAsync(
        ServiceProvider provider, MemorySettings settings, string messageId, CancellationToken cancellationToken)
    {
        var driver = provider.GetRequiredService<global::Neo4j.Driver.IDriver>();
        var session = driver.AsyncSession(o => o.WithDatabase(settings.Database)
            .WithDefaultAccessMode(global::Neo4j.Driver.AccessMode.Read));
        await using (session.ConfigureAwait(false))
        {
            return await session.ExecuteReadAsync(async transaction =>
            {
                // Dictionary parameters: the driver's object-to-map conversion is not needed (B6).
                var parameters = new Dictionary<string, object> { ["messageId"] = messageId };
                var items = new List<WrittenItem>();
                var cursor = await transaction.RunAsync(ItemsQuery, parameters).ConfigureAwait(false);
                while (await cursor.FetchAsync().ConfigureAwait(false))
                {
                    var record = cursor.Current;
                    items.Add(new WrittenItem(
                        (string)record["id"], (string)record["kind"], record["owner"] as string, record["live"] is true,
                        record["dims"] is long dims ? dims : 0));
                }
                var closed = new List<string>();
                cursor = await transaction.RunAsync(ClosedQuery, parameters).ConfigureAwait(false);
                while (await cursor.FetchAsync().ConfigureAwait(false))
                    closed.Add((string)cursor.Current["id"]);
                return new WrittenEvidence(items, closed);
            }).ConfigureAwait(false);
        }
    }
}
