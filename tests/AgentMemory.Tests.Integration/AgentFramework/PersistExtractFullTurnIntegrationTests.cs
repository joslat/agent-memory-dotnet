using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.AgentFramework;

/// <summary>
/// Live-Neo4j proof of #89's core acceptance criterion: a preference stated ONLY in the user's request
/// (never repeated by the assistant) is extracted and later recallable in a brand-new session, using a
/// real, DI-resolved <see cref="Neo4jMemoryContextProvider"/> against real Neo4j.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class PersistExtractFullTurnIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public PersistExtractFullTurnIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();

        // Deterministic preference extractor registered BEFORE AddNeo4jAgentMemory so it wins the
        // TryAddScoped override -- same convention as ExtractionOwnerStampIntegrationTests.cs.
        services.AddSingleton<IPreferenceExtractor, DeterministicPreferenceExtractor>();

        services.AddNeo4jAgentMemory(
            configureMemory: _ => { },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddAgentMemoryFramework(o => o.AutoExtractOnPersist = true);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(
                sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task PreferenceStatedOnlyByUser_IsExtractedAndRecallableInANewSession()
    {
        // StubEmbeddingGenerator is deterministic (same text -> same vector); recall is a similarity
        // search (MemoryContextAssembler -> SearchPreferencesAsync), so the recall query below
        // deliberately reuses this exact marker text to guarantee a match, matching the
        // "identical embeddings" convention used elsewhere in this test suite
        // (e.g. McpResourceIsolationIntegrationTests' ContextResource tests).
        const string preferenceMarker = "I prefer dark mode everywhere.";

        using var writeScope = _provider.CreateScope();
        var writeSp = writeScope.ServiceProvider;
        var memoryProvider = writeSp.GetRequiredService<Neo4jMemoryContextProvider>();

        // The assistant's reply never repeats the preference -- extraction must still capture it because
        // it now sees the request message too (#89), not just the response.
        await memoryProvider.PerformStoreAsync(
            requestMessages: [new ChatMessage(ChatRole.User, preferenceMarker)],
            responseMessages: [new ChatMessage(ChatRole.Assistant, "Got it.")],
            sessionId: "session-full-turn",
            conversationId: "conv-full-turn",
            cancellationToken: CancellationToken.None,
            userId: "alice");

        // Recall in a brand-new session for the same owner.
        using var recallScope = _provider.CreateScope();
        var recallSp = recallScope.ServiceProvider;
        var recallProvider = recallSp.GetRequiredService<Neo4jMemoryContextProvider>();

        var context = await recallProvider.BuildContextAsync(
            [new ChatMessage(ChatRole.User, preferenceMarker)],
            sessionId: "session-new",
            conversationId: "conv-new",
            cancellationToken: CancellationToken.None,
            userId: "alice");

        context.Messages.Should().NotBeNullOrEmpty();
        context.Messages!.Should().Contain(m => m.Text != null && m.Text.Contains("dark mode"),
            "a preference stated only in the user's request must be extracted and recallable in a later session");
    }

    private sealed class DeterministicPreferenceExtractor : IPreferenceExtractor
    {
        public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
        {
            var userMessage = messages.FirstOrDefault(m => m.Role == "user");
            if (userMessage is null || !userMessage.Content.Contains("prefer", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());

            return Task.FromResult<IReadOnlyList<ExtractedPreference>>(
            [
                new ExtractedPreference { Category = "style", PreferenceText = userMessage.Content, Confidence = 0.95 },
            ]);
        }
    }
}
