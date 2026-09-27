using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Security;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>Regressions from review round 4 (background extraction, I-7).</summary>
public sealed class ReviewRound4Tests
{
    // ---- M1: assemblies compiled against 1.5.0 still find their constructors ----

    public static TheoryData<Type, Type[]> ShippedConstructors => new()
    {
        { typeof(Neo4jMemoryContextProvider), [typeof(IMemoryService), typeof(IEmbeddingOrchestrator), typeof(IClock), typeof(IIdGenerator),
            typeof(IOptions<MemoryOptions>), typeof(IOptions<ContextFormatOptions>), typeof(IOptions<AgentFrameworkOptions>),
            typeof(ILogger<Neo4jMemoryContextProvider>), typeof(IMemoryStoreContext), typeof(IWritableMemoryOwnerContext),
            typeof(MemoryToolFactory), typeof(IAutomaticRecallPolicy), typeof(IMemoryContextAdmissionPolicy)] },
        { typeof(Neo4jChatHistoryProvider), [typeof(IMemoryService), typeof(IClock), typeof(IIdGenerator), typeof(AgentFrameworkOptions),
            typeof(ILogger<Neo4jChatHistoryProvider>), typeof(IMemoryStoreContext), typeof(IWritableMemoryOwnerContext),
            typeof(IMemoryContextAdmissionPolicy)] },
        { typeof(Neo4jMicrosoftMemoryFacade), [typeof(IMemoryService), typeof(Neo4jChatMessageStore), typeof(IOptions<AgentFrameworkOptions>),
            typeof(ILogger<Neo4jMicrosoftMemoryFacade>), typeof(IMemoryContextAdmissionPolicy)] },
        { typeof(AgentTraceRecorder), [typeof(IReasoningMemoryService), typeof(IClock), typeof(IIdGenerator),
            typeof(IOptions<AgentFrameworkOptions>), typeof(ILogger<AgentTraceRecorder>)] },
    };

    [Theory]
    [MemberData(nameof(ShippedConstructors))]
    public void The_1_5_0_constructor_still_exists(Type type, Type[] parameters)
    {
        // Adding an optional parameter changes the signature; code compiled against 1.5.0 would throw
        // MissingMethodException without the old one.
        type.GetConstructor(parameters).Should().NotBeNull();
    }

    // ---- M4: queued work gets its services from a fresh scope ----

    [Fact]
    public async Task Queued_extraction_uses_a_fresh_scope_not_the_turns()
    {
        // The turn's scope (an ASP.NET request) has usually ended when the work runs; a scoped disposable
        // dependency of the turn's IMemoryService would be disposed.
        var fresh = Substitute.For<IMemoryService>();
        var ran = new TaskCompletionSource();
        fresh.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { ran.TrySetResult(); return Task.FromResult(new ExtractionResult { SourceMessageIds = [] }); });
        var services = new ServiceCollection();
        services.AddScoped(_ => fresh);
        await using var provider = services.BuildServiceProvider();
        await using var queue = new BackgroundExtractionQueue(1, TimeSpan.FromSeconds(10),
            NullLogger<BackgroundExtractionQueue>.Instance, provider.GetRequiredService<IServiceScopeFactory>());
        var turns = Substitute.For<IMemoryService>();

        await TurnExtraction.ExtractAsync(turns, new ExtractionRequest { Messages = [new Message { MessageId = "m1", SessionId = "s1", ConversationId = "c1", Role = "user", Content = "I moved to Porto.", TimestampUtc = DateTimeOffset.UnixEpoch }], SessionId = "s1" },
            new AgentFrameworkOptions { ExtractInBackground = true }, queue, NullLogger.Instance, CancellationToken.None);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await turns.DidNotReceiveWithAnyArgs().ExtractAndPersistAsync(default!, default);
    }

    // ---- L5 (I-7): no edge from a node to itself ----

    [Fact]
    public async Task A_relationship_whose_ends_are_one_person_is_skipped()
    {
        var relationships = Substitute.For<IRelationshipRepository>();
        var entities = Substitute.For<IEntityRepository>();
        entities.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Entity>());
        var facts = Substitute.For<IFactRepository>();
        facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<Fact>());
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[4]);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        var sut = new PersistenceStage(embeddings, entities, facts, Substitute.For<IPreferenceRepository>(), relationships,
            Substitute.For<IClock>(), ids, NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions { ResolveUserToName = true, EnableBatchMemoryUpserts = false }));
        var ana = new Entity { EntityId = "entity-ana", Name = "Ana", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };

        await sut.PersistAsync(new ExtractionStageResult
        {
            SourceMessageIds = ["m1"],
            ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase) { ["Ana"] = ana },
            FilteredFacts = [new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Ana", Confidence = 0.9 }],
            FilteredRelationships = [new ExtractedRelationship { SourceEntity = "user", TargetEntity = "Ana", RelationshipType = "KNOWS", Confidence = 0.9 }],
        }, ownerId: "owner-1", cancellationToken: CancellationToken.None);

        await relationships.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }
}
