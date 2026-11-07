using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The look-ahead reads against a real Neo4j: what starts soon, what is dated inside a period, the dated fact an
/// anchored window takes its date from, and what overlaps given windows. Bruna's facts, asked on Friday 4 December 2026.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class LookAheadFactsIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly MemoryScope Bruna = MemoryScope.For("bruna", includeShared: false);
    private static readonly DateTimeOffset Now = new(2026, 12, 4, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset On(int month, int day) => new(2026, month, day, 0, 0, 0, TimeSpan.Zero);

    public LookAheadFactsIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact Make(
        string subject, string predicate, string @object,
        DateTimeOffset? occurredOn = null, DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null,
        DateTimeOffset? createdAt = null, string owner = "bruna") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = subject,
        Predicate = predicate,
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = createdAt ?? On(9, 7),
        OwnerId = owner,
        OccurredOn = occurredOn,
        ValidFrom = validFrom,
        ValidUntil = validUntil,
    };

    private async Task SeedAsync()
    {
        await _facts.UpsertAsync(Make("Bruna", "is leading the go-live of", "Acme's billing system", occurredOn: On(12, 11)));
        await _facts.UpsertAsync(Make("Bruna", "will be in", "Bariloche", validFrom: On(12, 9), validUntil: On(12, 13).AddDays(1).AddSeconds(-1)));
        await _facts.UpsertAsync(Make("the billing system", "has a code freeze starting", "December 4", validFrom: On(12, 4)));
        await _facts.UpsertAsync(Make("Bruna", "works at", "Nexo", validFrom: On(1, 10)));   // valid since long ago, no end
        await _facts.UpsertAsync(Make("Bruna", "drinks", "mate"));                             // undated
        await _facts.UpsertAsync(Make("Bruna", "wants to give a talk at", "a meetup", occurredOn: On(12, 30)));
        await _facts.UpsertAsync(Make("someone else", "has a go-live on", "December 10", occurredOn: On(12, 10), owner: "carol"));
    }

    [Fact]
    public async Task UpcomingIsWhatStartsAheadInTheWindowEarliestFirstAndOnlyTheOwners()
    {
        await SeedAsync();

        var upcoming = await _facts.GetUpcomingFactsAsync(Now, Now.AddDays(14), 10, Bruna);

        // The trip (a stay, by valid_from) and the go-live (an event, by occurred_on); not the freeze (it started at
        // midnight, before now), not the meetup (beyond 14 days), not the undated, not Carol's go-live.
        upcoming.Select(f => f.Object).Should().Equal("Bariloche", "Acme's billing system");
    }

    [Fact]
    public async Task AWindowHoldsTheDatedFactsInsideItAndNotWhatIsMerelyStillTrue()
    {
        await SeedAsync();

        var week = await _facts.GetFactsInWindowAsync(On(12, 7), On(12, 14).AddTicks(-1), 10, Bruna);

        // Bruna has worked at Nexo since January with no end: true all week, but not something dated in it.
        week.Select(f => f.Object).Should().Equal("Bariloche", "Acme's billing system");
    }

    [Fact]
    public async Task ABoundedStayThatSpansTheWindowIsInIt()
    {
        await SeedAsync();

        var oneDay = await _facts.GetFactsInWindowAsync(On(12, 10), On(12, 11).AddTicks(-1), 10, Bruna);

        oneDay.Select(f => f.Object).Should().Equal("Bariloche");
    }

    [Fact]
    public async Task TheAnchorIsTheDatedFactThatNamesItWhicheverWayItIsSpelled()
    {
        await SeedAsync();
        // A fact learned on the day of its own date ranks after a date the person stated.
        await _facts.UpsertAsync(Make("Diego", "will open the office on", "go-live day",
            occurredOn: On(11, 26), createdAt: On(11, 26).AddHours(10)));

        var anchor = await _facts.FindAnchorFactAsync("go-live", Now, Bruna);
        var spaced = await _facts.FindAnchorFactAsync("go live", Now, Bruna);

        anchor!.Object.Should().Be("Acme's billing system");
        anchor.OccurredOn.Should().Be(On(12, 11));
        spaced!.FactId.Should().Be(anchor.FactId, "a hyphen and a space are the same word");
        (await _facts.FindAnchorFactAsync("wedding", Now, Bruna)).Should().BeNull();
    }

    [Fact]
    public async Task AnAnchorDatedOnlyInItsWordsIsReadFromThem()
    {
        // The Conversational extractors stored the go-live's date in the object only.
        await _facts.UpsertAsync(Make("Acme Logistics billing system go-live", "is scheduled for", "2026-12-11"));
        await _facts.UpsertAsync(Make("Martín", "approved", "the go-live budget", occurredOn: On(11, 19), createdAt: On(11, 19).AddHours(10)));

        var anchor = await _facts.FindAnchorFactAsync("go-live", Now, Bruna);

        anchor!.Object.Should().Be("2026-12-11");
        anchor.OccurredOn.Should().Be(On(12, 11), "the date its words state");
    }

    [Fact]
    public async Task NewerUndatedMentionsDoNotHideAnOlderDatedAnchor()
    {
        // The dated go-live, then 30 later mentions with numbers but no date ("needs 7 more testers"): the date is still found.
        await _facts.UpsertAsync(Make("Bruna", "is leading the go-live of", "Acme's billing system", occurredOn: On(12, 11)));
        for (var i = 0; i < 30; i++)
            await _facts.UpsertAsync(Make("go-live", $"needs {i + 2} more testers", "before the cutover", createdAt: On(11, 1).AddHours(i)));

        var anchor = await _facts.FindAnchorFactAsync("go-live", Now, Bruna);

        anchor!.OccurredOn.Should().Be(On(12, 11));
    }

    [Fact]
    public async Task AnAnchorWithOnlyTheDaysItWasMentionedHasNoDate()
    {
        // "Martín approved the go-live budget", stamped the day it was said: no date of the go-live's. No window is
        // better than the wrong week.
        await _facts.UpsertAsync(Make("Martín", "approved", "the go-live budget", occurredOn: On(11, 19), createdAt: On(11, 19).AddHours(10)));
        await _facts.UpsertAsync(Make("Bruna", "needs to write", "the go-live runbook"));

        (await _facts.FindAnchorFactAsync("go-live", Now, Bruna)).Should().BeNull();
    }

    [Fact]
    public async Task OverlapIsWhatIsDatedInsideAnyWindowLeavingOutWhatIsAlreadyThere()
    {
        await SeedAsync();
        var goLive = (await _facts.GetFactsInWindowAsync(On(12, 11), On(12, 12).AddTicks(-1), 10, Bruna))
            .Single(f => f.Object == "Acme's billing system");

        var overlapping = await _facts.GetOverlappingFactsAsync(
            [new TimeWindow { StartUtc = On(12, 11), EndUtc = On(12, 12).AddTicks(-1) }],
            [goLive.FactId], 5, Bruna);

        overlapping.Select(f => f.Object).Should().Equal("Bariloche");
    }

    [Fact]
    public async Task NoWindowsReadNothing() =>
        (await _facts.GetOverlappingFactsAsync([], [], 5, Bruna)).Should().BeEmpty();
}
