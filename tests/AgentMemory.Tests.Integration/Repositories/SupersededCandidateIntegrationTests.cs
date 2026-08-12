using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The store side of write-time supersession (M1): which facts a new assertion replaces, live.
/// </summary>
/// <remarks>
/// <para>
/// The unit falsifier proves the <i>decision</i> — only functional relations, never the multi-valued
/// majority. This proves the <i>selection</i>, which is entirely Cypher and could not be checked with
/// a substitute: canonical-key matching, the liveness filter, owner confinement, and the exclusion of
/// the winner itself.
/// </para>
/// <para>
/// Liveness is the one that has to run here. <c>invalidated_at</c> is not carried on the domain
/// record, so an already-closed fact is indistinguishable from a live one to any in-memory filter —
/// and re-selecting closed facts would fan a supersession chain into a star, one new
/// <c>:SUPERSEDED_BY</c> edge per subsequent arrival.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class SupersededCandidateIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    public SupersededCandidateIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Fact> StoreAsync(
        string subject, string predicate, string @object, string? ownerId = "alice") =>
        _facts.UpsertAsync(new Fact
        {
            FactId = $"fact-{Guid.NewGuid():N}",
            Subject = subject,
            Predicate = predicate,
            Object = @object,
            Confidence = 0.9,
            OwnerId = ownerId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

    private Task<IReadOnlyList<Fact>> CandidatesFor(Fact winner, bool scoped = true) =>
        _facts.FindSupersededCandidatesAsync(
            winner.FactId, winner.Subject, winner.Predicate, winner.Object,
            scoped ? MemoryScope.For("alice", includeShared: false) : null);

    [Fact]
    public async Task TheEarlierAssertionIsSelected()
    {
        await StoreAsync("user", "lives in", "Basel");
        var winner = await StoreAsync("user", "lives in", "Zurich");

        var candidates = await CandidatesFor(winner);

        candidates.Should().ContainSingle().Which.Object.Should().Be("Basel");
    }

    [Fact]
    public async Task TheWinnerIsNeverItsOwnCandidate()
    {
        // A self-supersede would invalidate a live node and create a :SUPERSEDED_BY self-loop while
        // reporting success. The Cypher rejects it, but the selection must not offer it either.
        var winner = await StoreAsync("user", "lives in", "Zurich");

        (await CandidatesFor(winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARestatementInDifferentWordsIsNotACandidate()
    {
        // Matching is on canonical keys, the same ones the write path MERGEs on. If casing or spacing
        // produced a different key, a fact would supersede itself under a second identity.
        await StoreAsync("User", "Lives In", "Zurich");
        var winner = await StoreAsync("user", "lives in", "zurich");

        (await CandidatesFor(winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task AlreadySupersededFactsAreNotSelectedAgain()
    {
        // THE liveness property, and the reason this filter lives in Cypher. Without it, a third
        // assertion would re-close the first -- harmless to its invalidated_at, which coalesce
        // protects, but adding a second :SUPERSEDED_BY edge and turning a chain into a fan.
        var first = await StoreAsync("user", "lives in", "Basel");
        var second = await StoreAsync("user", "lives in", "Bern");
        await _facts.SupersedeAsync(first.FactId, second.FactId, MemoryScope.For("alice", includeShared: false));

        var third = await StoreAsync("user", "lives in", "Zurich");
        var candidates = await CandidatesFor(third);

        candidates.Should().ContainSingle().Which.FactId.Should().Be(second.FactId,
            "only the live assertion is replaced; the one already closed stays closed");
    }

    [Fact]
    public async Task ADifferentPredicateIsNotACandidate()
    {
        await StoreAsync("user", "works at", "Acme");
        var winner = await StoreAsync("user", "lives in", "Zurich");

        (await CandidatesFor(winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task ADifferentSubjectIsNotACandidate()
    {
        await StoreAsync("bob", "lives in", "Basel");
        var winner = await StoreAsync("user", "lives in", "Zurich");

        (await CandidatesFor(winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnotherOwnersFactIsNeverACandidate()
    {
        // Supersession closes a fact. Reaching across owners would close one belonging to somebody who
        // was not in the conversation -- the worst shape this feature could take.
        await StoreAsync("user", "lives in", "Basel", ownerId: "bob");
        var winner = await StoreAsync("user", "lives in", "Zurich");

        (await CandidatesFor(winner)).Should().BeEmpty();
    }

    [Fact]
    public async Task SupersessionIsNonDestructiveAndTheLoserRemainsReadable()
    {
        // "Fewer live facts" is only a win if nothing was lost. The loser keeps its content and its id;
        // what changes is that it leaves live recall.
        var loser = await StoreAsync("user", "lives in", "Basel");
        var winner = await StoreAsync("user", "lives in", "Zurich");

        await _facts.SupersedeAsync(loser.FactId, winner.FactId, MemoryScope.For("alice", includeShared: false));

        var stored = await _facts.GetByIdAsync(loser.FactId);
        stored.Should().NotBeNull();
        stored!.Object.Should().Be("Basel");
    }
}
