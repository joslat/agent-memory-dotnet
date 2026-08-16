using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.4 <b>GUARD G3</b> — the null-owner skip, which is TCK-load-bearing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test exists, in one paragraph.</b> The TCK bridge's <c>/add_fact</c> and
/// <c>/add_preference</c> endpoints route through <c>LongTermMemoryService</c>, so the working-memory
/// rebuild epilogue fires during a conformance run whenever this extension is on. <b>Bridge writes are
/// ownerless.</b> Without the skip, the rebuild reaches <c>MERGE (:User {identifier: null})</c> — and
/// a null unique key turns Bronze <i>and</i> Gold cases into 500s. The extension would break upstream
/// parity for everyone who enabled it, and the TCK run would be the first thing to notice.
/// </para>
/// <para>
/// The guard is one <c>string.IsNullOrWhiteSpace</c> check, which is exactly the kind of line a future
/// simplification deletes as redundant. So it is tested against a live database at the seam it
/// protects, and the test says why in its name.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class WorkingMemoryG3GuardIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;

    public WorkingMemoryG3GuardIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedIds : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    private Neo4jWorkingMemoryService Service(bool enabled)
    {
        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = enabled;
        // mention_count is absent on facts written by the plain repository path, and coalesce(...) = 1,
        // so a floor of 1 is what lets this fixture's facts earn a slot at all.
        options.WorkingMemory.MinFactMentionCount = 1;

        return new Neo4jWorkingMemoryService(
            _fixture.TransactionRunner,
            new FixedClock(),
            new FixedIds(),
            Options.Create(options),
            NullLogger<Neo4jWorkingMemoryService>.Instance);
    }

    private Task<long> CountUserNodesAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (u:User) RETURN count(u) AS c");
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnOwnerlessRebuildIsSkippedAndCreatesNoUserNode(string? ownerId)
    {
        // The TCK shape: a write with no owner. It must be a no-op, not a MERGE on a null key.
        var service = Service(enabled: true);

        var rebuild = async () => await service.RebuildAsync(ownerId!);

        await rebuild.Should().NotThrowAsync(
            "an ownerless write is what every TCK bridge write is; throwing here turns Bronze and Gold "
            + "cases into 500s");
        (await CountUserNodesAsync()).Should().Be(0,
            "there is no :User for the shared bucket, and inventing one would be guessing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnOwnerlessGetReturnsNullRatherThanQuerying(string? ownerId)
    {
        (await Service(enabled: true).GetAsync(ownerId!)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AnOwnerlessClearIsAlsoSkipped(string? ownerId)
    {
        var clear = async () => await Service(enabled: true).ClearAsync(ownerId!);

        await clear.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WithTheTierDisabledNothingIsWrittenEvenForARealOwner()
    {
        // The off-state: registered, resolvable, and inert.
        await Service(enabled: false).RebuildAsync("alice");

        (await CountUserNodesAsync()).Should().Be(0);
        (await Service(enabled: false).GetAsync("alice")).Should().BeNull();
    }

    [Fact]
    public async Task ARealOwnerDoesGetAUserNodeSoTheGuardIsNotJustAlwaysSkipping()
    {
        // The other half of the guard: proving the skip is CONDITIONAL. Without this, a service that
        // skipped everything would pass every test above.
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "CREATE (f:Fact {id: $id, owner_id: 'alice', subject: 'user', predicate: 'name', "
                + "object: 'Alice', confidence: 0.9, created_at: datetime()})",
                new { id = Guid.NewGuid().ToString("N") });
        });

        await Service(enabled: true).RebuildAsync("alice");

        (await CountUserNodesAsync()).Should().Be(1);
        var block = await Service(enabled: true).GetAsync("alice");
        block.Should().NotBeNull();
        block!.Text.Should().Contain("user name Alice");
    }

    [Fact]
    public async Task TheUserNodeIsKeyedByUpstreamsIdentifierProperty()
    {
        // The design proposed keying on owner_id and inventing a user_owner_unique constraint. The
        // snapshot check it mandated says upstream keys :User on `identifier` via `user_identifier`,
        // and adopting a label while keying it differently makes the adoption nominal -- the same
        // spelling carrying a different meaning, which the parity verifier cannot catch.
        await Service(enabled: true).RebuildAsync("alice");

        var keyed = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (u:User {identifier: 'alice'}) RETURN u.owner_id AS ownerId");
            var records = await cursor.ToListAsync();
            return records.Count == 1
                ? global::Neo4j.Driver.ValueExtensions.As<string>(records[0]["ownerId"])
                : null;
        });

        keyed.Should().Be("alice", "owner_id is written alongside upstream's key, and the two agree");
    }
}
