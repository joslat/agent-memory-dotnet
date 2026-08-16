using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The persist path rebuilds the working-memory block — design step 287, which the tier shipped without.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists.</b> The rebuild hook landed only on <c>LongTermMemoryService</c>'s
/// single-add methods. Extraction persists through <see cref="PersistenceStage"/> and never touched it,
/// so a host that enabled the tier and then ingested normally — conversation → extraction → persist,
/// which is exactly what the MAF adapter does — compiled no block, ever. Recall fetched null forever
/// while the feature read as enabled.
/// </para>
/// <para>
/// It survived a green suite and an independent gate review because <b>every</b> working-memory test
/// called <c>RebuildAsync</c> directly. Testing the thing rather than its trigger cannot detect a
/// trigger wired to the wrong path, which is the repository's own stated lesson about targeting the
/// trigger and not the happy path.
/// </para>
/// </remarks>
public sealed class PersistenceStageWorkingMemoryTests
{
    private readonly IWorkingMemoryService _workingMemory = Substitute.For<IWorkingMemoryService>();

    private PersistenceStage CreateSut(MemoryOptions? memoryOptions = null, bool registerTier = true)
    {
        var entityRepository = Substitute.For<IEntityRepository>();
        var factRepository = Substitute.For<IFactRepository>();
        var preferenceRepository = Substitute.For<IPreferenceRepository>();
        var relationshipRepository = Substitute.For<IRelationshipRepository>();
        var embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        var clock = Substitute.For<IClock>();
        var idGenerator = Substitute.For<IIdGenerator>();

        clock.UtcNow.Returns(DateTimeOffset.Parse("2026-08-16T00:00:00Z"));
        idGenerator.GenerateId().Returns(call => $"id-{Guid.NewGuid():N}");
        embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[4]);
        entityRepository.UpsertAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Entity>());
        factRepository.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Fact>());
        preferenceRepository.UpsertAsync(Arg.Any<Preference>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Preference>());

        var options = memoryOptions ?? Enabled();

        return new PersistenceStage(
            embeddingOrchestrator,
            entityRepository,
            factRepository,
            preferenceRepository,
            relationshipRepository,
            clock,
            idGenerator,
            NullLogger<PersistenceStage>.Instance,
            new PassThroughMemoryPersistenceTransaction(),
            Options.Create(new ExtractionOptions()),
            registerTier ? _workingMemory : null,
            Options.Create(options));
    }

    private static MemoryOptions Enabled()
    {
        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        return options;
    }

    private static ExtractionStageResult WithOneFact() => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
        FilteredFacts =
        [
            new ExtractedFact { Subject = "Alice", Predicate = "works_at", Object = "Acme", Confidence = 0.9 }
        ],
        FilteredPreferences = [],
        FilteredRelationships = [],
    };

    private static ExtractionStageResult Empty() => new()
    {
        SourceMessageIds = ["message-1"],
        ResolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
        FilteredFacts = [],
        FilteredPreferences = [],
        FilteredRelationships = [],
    };

    [Fact]
    public async Task APersistThatStoresSomething_RebuildsTheBlock_Once()
    {
        await CreateSut().PersistAsync(WithOneFact(), ownerId: "alice");

        await _workingMemory.Received(1).RebuildAsync("alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APersistThatStoresNothing_DoesNotRebuild()
    {
        // Nothing landed, so nothing about the block can have changed. Rebuilding anyway would spend a
        // read round trip per no-op ingestion.
        await CreateSut().PersistAsync(Empty(), ownerId: "alice");

        await _workingMemory.DidNotReceive().RebuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOwnerlessPersist_DoesNotRebuild()
    {
        // Guard G3. The block is per-owner; rebuilding for a null owner would either throw or compile
        // somebody's block from everybody's memories.
        await CreateSut().PersistAsync(WithOneFact(), ownerId: null);

        await _workingMemory.DidNotReceive().RebuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheTierOff_DoesNotRebuild()
    {
        await CreateSut(new MemoryOptions()).PersistAsync(WithOneFact(), ownerId: "alice");

        await _workingMemory.DidNotReceive().RebuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebuildOnWriteOff_DoesNotRebuild()
    {
        var options = Enabled();
        options.WorkingMemory.RebuildOnWrite = false;

        await CreateSut(options).PersistAsync(WithOneFact(), ownerId: "alice");

        await _workingMemory.DidNotReceive().RebuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARebuildFailure_DoesNotFailThePersist_AndClearsTheStaleBlock()
    {
        // The write already succeeded and the caller is not waiting on a derived projection, so the
        // failure must not propagate. Staleness is worse than absence: a block asserting a superseded
        // value manufactures exactly the knowledge-update failures the tier exists to prevent.
        _workingMemory.RebuildAsync("alice", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rebuild exploded"));

        var persist = async () => await CreateSut().PersistAsync(WithOneFact(), ownerId: "alice");

        await persist.Should().NotThrowAsync();
        await _workingMemory.Received(1).ClearAsync("alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoTierRegistered_PersistsExactlyAsBefore()
    {
        // A host that never registered the tier keeps the previous construction and behaviour.
        var persist = async () =>
            await CreateSut(registerTier: false).PersistAsync(WithOneFact(), ownerId: "alice");

        await persist.Should().NotThrowAsync();
    }
}
