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
}

/// <summary>What one remember wrote: the stored message and what the writer extracted from it.</summary>
internal sealed record RememberOutcome(Message Message, ExtractionResult Extraction);

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
                }, cancellationToken).ConfigureAwait(false);
                return new RememberOutcome(message, extraction);
            }
        }
    }
}
