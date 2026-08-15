using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.7 step 4, against a live graph: a fact fires when it becomes true, once, and only for its owner.
/// </summary>
/// <remarks>
/// <para>
/// The counter that matters most here is <b>premature surfacing</b>: a not-yet-valid fact appearing in
/// assembled context is worse than one that never fires, because it is a confident statement about a
/// world that does not exist yet. It has its own test below, and it is the one this feature would be
/// withdrawn over.
/// </para>
/// <para>
/// The window is half-open on the same convention as every other window query in this codebase, so a
/// fact that fired in one recall does not fire again in the next one anchored at the same instant.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ProspectiveDueFactsIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public ProspectiveDueFactsIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact Make(
        string @object,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        string owner = "alice") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "subscription",
        Predicate = "renews",
        Object = @object,
        Confidence = 0.9,
        // Created long ago on purpose: firing must read the VALID-time clock, so a fact learned months
        // before it becomes true still has to fire on the day it becomes true.
        CreatedAtUtc = Now.AddDays(-200),
        OwnerId = owner,
        ValidFrom = validFrom,
        ValidUntil = validUntil,
    };

    private Task<ProspectiveDueResult> FireAsync(
        TimeSpan? lookback = null, TimeSpan? expiring = null, int limit = 10) =>
        _facts.GetDueFactsAsync(
            Now - (lookback ?? TimeSpan.FromDays(7)), Now,
            expiring ?? TimeSpan.FromDays(7), limit, Alice);

    // ── due ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AFactThatBecameTrueInsideTheWindowFires()
    {
        await _facts.UpsertAsync(Make("today", validFrom: Now.AddDays(-1)));

        var result = await FireAsync();

        result.Due.Should().ContainSingle().Which.Object.Should().Be("today");
    }

    [Fact]
    public async Task AFactThatBecameTrueBeforeTheWindowDoesNotFireAgain()
    {
        // Firing once is the whole contract. A fact that fired last week must not fire every week
        // thereafter, or the section becomes noise the reader learns to skip.
        await _facts.UpsertAsync(Make("last month", validFrom: Now.AddDays(-40)));

        var result = await FireAsync();

        result.Due.Should().BeEmpty();
    }

    [Fact]
    public async Task AFactThatIsNotYetTrueNeverFires()
    {
        // THE counter this feature would be withdrawn over. A not-yet-valid fact in assembled context
        // is a confident statement about a world that does not exist yet.
        await _facts.UpsertAsync(Make("next year", validFrom: Now.AddDays(90)));

        var result = await FireAsync();

        result.Due.Should().BeEmpty();
        result.Expiring.Should().BeEmpty();
    }

    [Fact]
    public async Task AFactWithNoValidFromNeverFires()
    {
        // Most extracted facts carry no valid time -- the Extract prompt omits rather than guesses.
        // Firing on them would turn "we do not know when this became true" into "it just became true".
        await _facts.UpsertAsync(Make("timeless"));

        var result = await FireAsync();

        result.Due.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInvalidatedFactNeverFiresEvenWhenItsWindowJustOpened()
    {
        var fact = await _facts.UpsertAsync(Make("retracted", validFrom: Now.AddDays(-1)));
        await _facts.InvalidateAsync(fact.FactId, Alice);

        var result = await FireAsync();

        result.Due.Should().BeEmpty();
    }

    [Fact]
    public async Task TheWindowIsHalfOpenAtTheLowerBound()
    {
        // valid_from == since is EXCLUDED, matching every other window query here, so consecutive
        // firings anchored at the same instant cannot both surface it.
        var since = Now.AddDays(-7);
        await _facts.UpsertAsync(Make("boundary", validFrom: since));

        var result = await FireAsync();

        result.Due.Should().BeEmpty();
    }

    // ── expiring ──────────────────────────────────────────────────────

    [Fact]
    public async Task AFactClosingSoonIsReportedAsExpiring()
    {
        await _facts.UpsertAsync(Make("passport", validFrom: Now.AddDays(-200), validUntil: Now.AddDays(3)));

        var result = await FireAsync();

        result.Expiring.Should().ContainSingle().Which.Object.Should().Be("passport");
    }

    [Fact]
    public async Task AnAlreadyExpiredFactIsNotReportedAsExpiring()
    {
        // Already-expired belongs to delta recall's expired-validity bucket. Calling it "expiring" is a
        // tense error the reader acts on.
        await _facts.UpsertAsync(Make("lapsed", validFrom: Now.AddDays(-200), validUntil: Now.AddDays(-1)));

        var result = await FireAsync();

        result.Expiring.Should().BeEmpty();
    }

    [Fact]
    public async Task AFactClosingBeyondTheHorizonIsNotReportedYet()
    {
        await _facts.UpsertAsync(Make("distant", validFrom: Now.AddDays(-200), validUntil: Now.AddDays(60)));

        var result = await FireAsync();

        result.Expiring.Should().BeEmpty();
    }

    // ── isolation and caps ────────────────────────────────────────────

    [Fact]
    public async Task AnotherOwnersDueFactNeverFires()
    {
        await _facts.UpsertAsync(Make("mine", validFrom: Now.AddDays(-1), owner: "alice"));
        await _facts.UpsertAsync(Make("theirs", validFrom: Now.AddDays(-1), owner: "bob"));

        var result = await FireAsync();

        result.Due.Should().ContainSingle().Which.Object.Should().Be("mine");
    }

    [Fact]
    public async Task TheBudgetIsHonouredAndKeepsTheMostRecentlyDue()
    {
        // LIMIT truncates the OLDEST: if only some reminders fit, the ones that just became true are
        // the ones worth the space.
        await _facts.UpsertAsync(Make("older", validFrom: Now.AddDays(-6)));
        await _facts.UpsertAsync(Make("newer", validFrom: Now.AddDays(-1)));

        var result = await FireAsync(limit: 1);

        result.Due.Should().ContainSingle().Which.Object.Should().Be("newer");
    }

    [Fact]
    public async Task NothingDueYieldsAnEmptyResultRatherThanAnError()
    {
        var result = await FireAsync();

        result.IsEmpty.Should().BeTrue();
    }
}
