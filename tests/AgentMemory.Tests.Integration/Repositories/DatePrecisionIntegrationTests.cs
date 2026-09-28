using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 36.1: a validity date keeps the precision it was stated at through every fact write path (single, batch,
/// fused), a re-assertion without a date keeps the stored one with its precision, and the profile block renders
/// it by the same rule as recall.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class DatePrecisionIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset March2024 = new(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End2027 = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1);

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    public DatePrecisionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact Dated(string id, string @object) => new()
    {
        FactId = id, Subject = "Nadia", Predicate = "lives in", Object = @object, Confidence = 0.9,
        OwnerId = "owner-dates", CreatedAtUtc = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
        ValidFrom = March2024, ValidFromPrecision = DatePrecision.Month,
        ValidUntil = End2027, ValidUntilPrecision = DatePrecision.Year,
    };

    private async Task ShouldReadBackAsync(string id)
    {
        var read = await _facts.GetByIdAsync(id);
        read.Should().NotBeNull();
        read!.ValidFrom.Should().Be(March2024);
        read.ValidFromPrecision.Should().Be(DatePrecision.Month);
        read.ValidUntilPrecision.Should().Be(DatePrecision.Year);
    }

    [Fact]
    public async Task The_single_write_keeps_the_precision()
    {
        await _facts.UpsertAsync(Dated("single", "Lyon"));
        await ShouldReadBackAsync("single");
    }

    [Fact]
    public async Task The_batch_write_keeps_the_precision()
    {
        await _facts.UpsertBatchAsync([Dated("batch", "Lyon")]);
        await ShouldReadBackAsync("batch");
    }

    [Fact]
    public async Task The_fused_write_keeps_the_precision()
    {
        await _facts.UpsertFusedBatchAsync([Dated("fused", "Lyon")]);
        await ShouldReadBackAsync("fused");
    }

    [Fact]
    public async Task A_re_assertion_without_a_date_keeps_the_date_and_its_precision()
    {
        await _facts.UpsertAsync(Dated("first", "Lyon"));
        await _facts.UpsertAsync(Dated("again", "Lyon") with
        {
            ValidFrom = null, ValidFromPrecision = DatePrecision.Unspecified,
            ValidUntil = null, ValidUntilPrecision = DatePrecision.Unspecified,
        });

        await ShouldReadBackAsync("first");
    }

    private static readonly DateTimeOffset Sept26 = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private static Fact Event(string id) => new()
    {
        FactId = id, Subject = "Lucas", Predicate = "went hiking in", Object = "Sintra", Confidence = 0.9,
        OwnerId = "owner-dates", CreatedAtUtc = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
        OccurredOn = Sept26, OccurredOnPrecision = DatePrecision.Day,
    };

    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("fused")]
    public async Task Every_write_path_keeps_the_day_an_event_happened(string path)
    {
        var fact = Event(path);
        await (path switch
        {
            "single" => _facts.UpsertAsync(fact),
            "batch" => _facts.UpsertBatchAsync([fact]),
            _ => (Task)_facts.UpsertFusedBatchAsync([fact]),
        });

        var read = await _facts.GetByIdAsync(path);
        read!.OccurredOn.Should().Be(Sept26);
        read.OccurredOnPrecision.Should().Be(DatePrecision.Day);
        read.ValidFrom.Should().BeNull();

        // Told again without a day, it keeps the day; told again with one, the latest telling wins.
        await _facts.UpsertAsync(Event("again") with { OccurredOn = null, OccurredOnPrecision = DatePrecision.Unspecified });
        (await _facts.GetByIdAsync(path))!.OccurredOn.Should().Be(Sept26);
    }

    [Fact]
    public async Task The_profile_block_reads_an_event_as_its_day_and_keeps_it_after_the_day()
    {
        await _facts.UpsertAsync(Event("event"));
        var options = new MemoryOptions();
        options.WorkingMemory.MinFactMentionCount = 1;
        options.WorkingMemory.IncludeDates = true;
        var service = new Neo4jWorkingMemoryService(
            _fixture.TransactionRunner, new FixedClock(), new Ids(), Options.Create(options),
            NullLogger<Neo4jWorkingMemoryService>.Instance);

        var block = await service.ComposeAsync("owner-dates", new FixedClock().UtcNow, CancellationToken.None);

        block.Should().Contain("Lucas went hiking in Sintra (on 2026-09-26)");
    }

    [Fact]
    public async Task A_fact_written_before_precision_was_recorded_reads_as_unspecified()
    {
        await _facts.UpsertAsync(Dated("legacy", "Lyon") with
        {
            ValidFromPrecision = DatePrecision.Unspecified, ValidUntilPrecision = DatePrecision.Unspecified,
        });

        var read = await _facts.GetByIdAsync("legacy");
        read!.ValidFromPrecision.Should().Be(DatePrecision.Unspecified);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_profile_block_carries_the_date_exactly_when_asked(bool includeDates)
    {
        await _facts.UpsertAsync(Dated("profile", "Lyon"));
        var options = new MemoryOptions();
        options.WorkingMemory.MinFactMentionCount = 1;
        options.WorkingMemory.IncludeDates = includeDates;
        var service = new Neo4jWorkingMemoryService(
            _fixture.TransactionRunner, new FixedClock(), new Ids(), Options.Create(options),
            NullLogger<Neo4jWorkingMemoryService>.Instance);

        var block = await service.ComposeAsync("owner-dates", new FixedClock().UtcNow, CancellationToken.None);

        if (includeDates)
            block.Should().Contain("Nadia lives in Lyon (2024-03 to 2027)");
        else
            block.Should().Contain("Nadia lives in Lyon").And.NotContain("2024");
    }
}
