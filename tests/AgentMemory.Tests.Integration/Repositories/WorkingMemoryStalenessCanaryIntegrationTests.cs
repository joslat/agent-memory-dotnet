using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.4 <b>the staleness canary</b> — this design's kill rule, as a fixture.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why staleness is the pre-registered kill rule rather than a footnote.</b> Structured recall
/// scores 8/9 on knowledge-update — it is the weakest measured non-episodic type. A profile block
/// asserting the OLD value of an updated fact would <i>manufacture</i> failures in exactly that type,
/// turning a feature meant to add deterministic coverage into a machine for producing the errors we
/// are worst at. The design states: if a knowledge-update question flips correct→wrong and the
/// transcript shows the block asserting a superseded value, the feature is killed as designed.
/// </para>
/// <para>
/// <b>This fixture is the contract that makes that rule enforceable before any spend.</b> It goes
/// through the <i>production</i> supersession path — not a hand-called rebuild — because the claim
/// being tested is "after the write call returns, the block is current", and only the real call site
/// can establish that. It is why the rebuild is awaited inline rather than fire-and-forget.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class WorkingMemoryStalenessCanaryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jWorkingMemoryService _workingMemory;
    private readonly LongTermMemoryService _longTerm;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    public WorkingMemoryStalenessCanaryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);

        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        options.WorkingMemory.MinFactMentionCount = 1;

        _workingMemory = new Neo4jWorkingMemoryService(
            fixture.TransactionRunner, new FixedClock(), new Ids(),
            Options.Create(options), NullLogger<Neo4jWorkingMemoryService>.Instance);

        _longTerm = new LongTermMemoryService(
            Substitute.For<AgentMemory.Abstractions.Repositories.IEntityRepository>(),
            _facts,
            Substitute.For<AgentMemory.Abstractions.Repositories.IPreferenceRepository>(),
            Substitute.For<AgentMemory.Abstractions.Repositories.IRelationshipRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            _workingMemory,
            Options.Create(options));
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact NewFact(string @object) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.95,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnerId = "alice",
    };

    [Fact]
    public async Task AfterSupersessionTheBlockCarriesTheNewValueAndNotTheOld()
    {
        // THE canary. Everything else in this feature is optional; this is the condition under which
        // it must not ship.
        var acme = await _facts.UpsertAsync(NewFact("Acme"));
        await _workingMemory.RebuildAsync("alice");

        (await _workingMemory.GetAsync("alice"))!.Text.Should().Contain("Acme");

        var globex = await _facts.UpsertAsync(NewFact("Globex"));

        // The PRODUCTION path, not a hand-called rebuild: the contract is "after the write call
        // returns, the block is current".
        (await _longTerm.SupersedeFactAsync(acme.FactId, globex.FactId, Alice)).Should().BeTrue();

        var block = await _workingMemory.GetAsync("alice");
        block.Should().NotBeNull();
        block!.Text.Should().Contain("Globex");
        block.Text.Should().NotContain("Acme",
            "a block asserting a superseded value manufactures failures in knowledge-update, the "
            + "weakest measured non-episodic type -- this is the design's kill rule");
    }

    [Fact]
    public async Task AddingAFactThroughTheServiceRefreshesTheBlockImmediately()
    {
        await _longTerm.AddFactAsync(NewFact("Acme"));

        (await _workingMemory.GetAsync("alice"))!.Text.Should().Contain("user works_at Acme");
    }

    [Fact]
    public async Task AnUnchangedRebuildDoesNotMoveTheBuiltAtStamp()
    {
        // The hash short-circuit: built_at moves only when the CONTENT moves. Without it every write
        // burst would churn a transaction and invalidate prompt-prefix caching for an identical block.
        await _facts.UpsertAsync(NewFact("Acme"));
        await _workingMemory.RebuildAsync("alice");
        var first = await _workingMemory.GetAsync("alice");

        await Task.Delay(1100);
        await _workingMemory.RebuildAsync("alice");
        var second = await _workingMemory.GetAsync("alice");

        second!.ContentHash.Should().Be(first!.ContentHash);
        second.BuiltAtUtc.Should().Be(first.BuiltAtUtc, "an unchanged rebuild writes nothing");
    }

    [Fact]
    public async Task AFutureDatedFactIsNotPartOfWhoTheUserIsToday()
    {
        // Validity gating: a fact that becomes true next month must not be asserted as current.
        await _facts.UpsertAsync(NewFact("Acme") with
        {
            ValidFrom = DateTimeOffset.UtcNow.AddMonths(1),
        });

        await _workingMemory.RebuildAsync("alice");

        (await _workingMemory.GetAsync("alice")).Should().BeNull("no line qualified, so no block exists");
    }

    [Fact]
    public async Task AnExpiredFactIsNotPartOfWhoTheUserIsToday()
    {
        await _facts.UpsertAsync(NewFact("Acme") with
        {
            ValidUntil = DateTimeOffset.UtcNow.AddDays(-1),
        });

        await _workingMemory.RebuildAsync("alice");

        (await _workingMemory.GetAsync("alice")).Should().BeNull();
    }

    [Fact]
    public async Task AnotherOwnersFactsNeverReachThisOwnersBlock()
    {
        await _facts.UpsertAsync(NewFact("Acme"));
        await _facts.UpsertAsync(NewFact("Globex") with { OwnerId = "bob", FactId = Guid.NewGuid().ToString("N") });

        await _workingMemory.RebuildAsync("alice");

        var block = await _workingMemory.GetAsync("alice");
        block!.Text.Should().Contain("Acme").And.NotContain("Globex");
    }

    [Fact]
    public async Task ClearRemovesTheBlockWithoutDeletingTheIdentityNode()
    {
        // Upstream owns :User. Deleting an identity node because our derived block failed to rebuild
        // would destroy something that is not ours to destroy.
        await _facts.UpsertAsync(NewFact("Acme"));
        await _workingMemory.RebuildAsync("alice");

        await _workingMemory.ClearAsync("alice");

        (await _workingMemory.GetAsync("alice")).Should().BeNull();
        var nodes = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (u:User {identifier: 'alice'}) RETURN count(u) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });
        nodes.Should().Be(1, "the identity node survives; only our derived properties are cleared");
    }
}
