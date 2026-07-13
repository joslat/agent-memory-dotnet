using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core.Stubs;
using NSubstitute;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// Mirrors the golden-path registration in docs/agent-framework.md's "Usage" section verbatim. If either
/// AddNeo4jAgentMemory's or AddAgentMemoryFramework's signature changes in a way that breaks this
/// registration, this test fails to compile or resolve — a signal that the doc sample has drifted and
/// must be updated alongside it.
/// </summary>
public sealed class GoldenPathDocumentationTests
{
    [Fact]
    public async Task DocsGoldenPath_SingleAddNeo4jAgentMemoryCall_RegistersAndResolvesMafAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mirrors docs/agent-framework.md step 1 — one call, no separate AddAgentMemoryCore.
        services.AddNeo4jAgentMemory(
            configureMemory: _ => { },
            configureNeo4j: neo4j =>
            {
                neo4j.Uri = "bolt://localhost:7687";
                neo4j.Username = "neo4j";
                neo4j.Password = "password";
            });

        // Mirrors docs/agent-framework.md step 2 — chat + embedding providers (stubs stand in for
        // "your OpenAI/Azure chat client" / "your embedding generator" in the doc).
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

        // Mirrors docs/agent-framework.md step 3 — the MAF adapter registration.
        services.AddAgentMemoryFramework(options =>
        {
            options.AutoExtractOnPersist = true;
            options.ContextFormat.IncludeEntities = true;
            options.ContextFormat.IncludeFacts = true;
            options.ContextFormat.IncludePreferences = true;
        });

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<Neo4jMemoryContextProvider>().Should().NotBeNull();
        sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions().Should().NotBeEmpty();
    }
}
