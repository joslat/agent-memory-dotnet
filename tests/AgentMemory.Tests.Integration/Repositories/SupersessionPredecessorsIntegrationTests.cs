using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.2 step 6. The supersession-predecessor read, against a live graph.
/// </summary>
/// <remarks>
/// The Cypher walks <c>(prev)-[:SUPERSEDED_BY]-&gt;(cur)</c> with <b>no owner clause</b>, on the
/// argument that <c>SupersedeAsync</c> guards both ends with a same-owner check so a chain cannot
/// cross owners in the first place. That argument is load-bearing for tenant isolation, so it is
/// tested here rather than believed — the whole point of writing the claim down was to make it
/// falsifiable.
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class SupersessionPredecessorsIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);
    private static readonly MemoryScope Bob = MemoryScope.For("bob", includeShared: false);

    public SupersessionPredecessorsIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact NewFact(string @object, string owner, string subject = "Bob") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = subject,
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnerId = owner,
    };

    [Fact]
    public async Task ASupersededFactIsReturnedAsAPredecessorOfTheWinner()
    {
        var loser = await _facts.UpsertAsync(NewFact("Globex", "alice"));
        var winner = await _facts.UpsertAsync(NewFact("Acme", "alice"));
        (await _facts.SupersedeAsync(loser.FactId, winner.FactId, Alice)).Should().BeTrue();

        var predecessors = await _facts.GetSupersessionPredecessorsAsync([winner.FactId], 3);

        predecessors.Should().ContainKey(winner.FactId);
        predecessors[winner.FactId].Should().ContainSingle()
            .Which.Object.Should().Be("Globex");
    }

    [Fact]
    public async Task BothClocksAreReadBack()
    {
        // Supersede stamps invalidated_at AND valid_until; rendering prefers valid time, so both must
        // survive the round trip or the note silently falls back to the wrong clock.
        var loser = await _facts.UpsertAsync(NewFact("Globex", "alice"));
        var winner = await _facts.UpsertAsync(NewFact("Acme", "alice"));
        await _facts.SupersedeAsync(loser.FactId, winner.FactId, Alice);

        var predecessor = (await _facts.GetSupersessionPredecessorsAsync([winner.FactId], 3))[winner.FactId][0];

        predecessor.InvalidatedAtUtc.Should().NotBeNull();
        predecessor.ValidUntilUtc.Should().NotBeNull();
        predecessor.EffectiveDate.Should().Be(predecessor.ValidUntilUtc);
    }

    [Fact]
    public async Task AChainIsReturnedNewestFirst()
    {
        // Order is what makes "(since D; previously X; earlier Y)" mean anything.
        var oldest = await _facts.UpsertAsync(NewFact("Initech", "alice"));
        var middle = await _facts.UpsertAsync(NewFact("Globex", "alice"));
        var current = await _facts.UpsertAsync(NewFact("Acme", "alice"));

        await _facts.SupersedeAsync(oldest.FactId, current.FactId, Alice);
        await Task.Delay(1100); // distinct second-resolution close stamps
        await _facts.SupersedeAsync(middle.FactId, current.FactId, Alice);

        var chain = (await _facts.GetSupersessionPredecessorsAsync([current.FactId], 5))[current.FactId];

        chain.Should().HaveCount(2);
        chain[0].Object.Should().Be("Globex", "the most recently closed predecessor comes first");
        chain[1].Object.Should().Be("Initech");
    }

    [Fact]
    public async Task TheChainIsCappedAtTheRequestedLength()
    {
        var a = await _facts.UpsertAsync(NewFact("A", "alice"));
        var b = await _facts.UpsertAsync(NewFact("B", "alice"));
        var current = await _facts.UpsertAsync(NewFact("Acme", "alice"));
        await _facts.SupersedeAsync(a.FactId, current.FactId, Alice);
        await _facts.SupersedeAsync(b.FactId, current.FactId, Alice);

        var chain = (await _facts.GetSupersessionPredecessorsAsync([current.FactId], 1))[current.FactId];

        chain.Should().ContainSingle("the per-fact cap is applied inside the query, not by the caller");
    }

    [Fact]
    public async Task AChainCannotCrossOwners()
    {
        // THE isolation claim the missing owner clause rests on. SupersedeAsync must refuse to link
        // two owners' facts at all -- so there is no cross-owner chain for this read to expose.
        var aliceFact = await _facts.UpsertAsync(NewFact("Globex", "alice"));
        var bobFact = await _facts.UpsertAsync(NewFact("Acme", "bob"));

        var linked = await _facts.SupersedeAsync(aliceFact.FactId, bobFact.FactId, scope: null);

        linked.Should().BeFalse("a cross-owner supersede must be refused even on the unscoped path");
        (await _facts.GetSupersessionPredecessorsAsync([bobFact.FactId], 3))
            .Should().NotContainKey(bobFact.FactId);
    }

    [Fact]
    public async Task OneAnotherOwnersSupersessionIsNotReturnedForThisOwnersFact()
    {
        // Belt and braces on the same claim, from the read side: alice's chain and bob's chain coexist
        // and neither appears under the other's id.
        var aliceOld = await _facts.UpsertAsync(NewFact("Globex", "alice"));
        var aliceNew = await _facts.UpsertAsync(NewFact("Acme", "alice"));
        await _facts.SupersedeAsync(aliceOld.FactId, aliceNew.FactId, Alice);

        var bobOld = await _facts.UpsertAsync(NewFact("Initech", "bob"));
        var bobNew = await _facts.UpsertAsync(NewFact("Umbrella", "bob"));
        await _facts.SupersedeAsync(bobOld.FactId, bobNew.FactId, Bob);

        var aliceChain = await _facts.GetSupersessionPredecessorsAsync([aliceNew.FactId], 5);
        var bobChain = await _facts.GetSupersessionPredecessorsAsync([bobNew.FactId], 5);

        aliceChain[aliceNew.FactId].Should().ContainSingle().Which.Object.Should().Be("Globex");
        bobChain[bobNew.FactId].Should().ContainSingle().Which.Object.Should().Be("Initech");
    }

    [Fact]
    public async Task AFactWithNoHistoryIsSimplyAbsentFromTheResult()
    {
        var fact = await _facts.UpsertAsync(NewFact("Acme", "alice"));

        (await _facts.GetSupersessionPredecessorsAsync([fact.FactId], 3)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyIdListIssuesNoQueryAndReturnsEmpty()
    {
        (await _facts.GetSupersessionPredecessorsAsync([], 3)).Should().BeEmpty();
    }

    [Fact]
    public async Task ManyFactsAreAnsweredByOneBatchedCall()
    {
        // The batching claim: ids for the whole section go in, a map comes back.
        var aOld = await _facts.UpsertAsync(NewFact("Globex", "alice", subject: "Bob"));
        var aNew = await _facts.UpsertAsync(NewFact("Acme", "alice", subject: "Bob"));
        await _facts.SupersedeAsync(aOld.FactId, aNew.FactId, Alice);

        var bOld = await _facts.UpsertAsync(NewFact("Zurich", "alice", subject: "Carol"));
        var bNew = await _facts.UpsertAsync(NewFact("Basel", "alice", subject: "Carol"));
        await _facts.SupersedeAsync(bOld.FactId, bNew.FactId, Alice);

        var result = await _facts.GetSupersessionPredecessorsAsync([aNew.FactId, bNew.FactId], 3);

        result.Should().HaveCount(2);
        result[aNew.FactId][0].Object.Should().Be("Globex");
        result[bNew.FactId][0].Object.Should().Be("Zurich");
    }
}
