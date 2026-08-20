using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// What the short-result rescue actually buys, and what it costs — measured on identical data.
/// </summary>
/// <remarks>
/// <para>
/// <c>MemoryOptions.RescueShortOwnerResults</c> is off by default, and the stated reason is
/// <i>measurement comparability</i>, not correctness: "every recorded measurement was taken without
/// it". That is a good reason to have shipped it off and a poor reason to leave it off forever,
/// because the number that would justify flipping it had never been taken.
/// </para>
/// <para>
/// This is that number. Same corpus, same query, same repository — only the flag differs. Yield is
/// the count returned against a limit the owner's own data could satisfy; cost is wall-clock for the
/// extra scoped scan. No model and no corpus build: starvation depends only on how many foreign rows
/// outrank the owner's inside the global top-K, so it is constructible in seconds.
/// </para>
/// <para>
/// <b>Why this matters beyond an option default.</b> An 11-point gap between our pipeline (0.21) and
/// a BM25 baseline (0.32) on TypedMemEval is currently unattributed, and the other party has offered
/// that their own calibration gate may be the cause. We hold a measured confound that produces the
/// same shape of number, and it is this one — so the honest move is to size it before anyone
/// attributes anything.
/// </para>
/// <para>
/// The assertions are deliberately weak: the rescue can never return fewer rows, and isolation holds
/// on both arms. Exact counts depend on tie-breaking inside Neo4j's index and pinning them would test
/// Neo4j rather than this behaviour — <b>the numbers in the test output are the deliverable.</b>
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ShortRescueYieldArmIntegrationTests : IAsyncLifetime
{
    // 50 owners x 4 facts reproduces the construction behind "a mean of 7 of 60"; at a global fetch
    // width of 60 the querying owner should expect roughly 60/50 rows before any rescue.
    private const int Owners = 50;
    private const int FactsPerOwner = 4;
    private const int Limit = 10;

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ShortRescueYieldArmIntegrationTests(
        Neo4jIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jFactRepository Repository(bool rescue) =>
        new(
            _fixture.TransactionRunner,
            NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { RescueShortOwnerResults = rescue }));

    /// <summary>
    /// Foreign facts are made strictly MORE similar to the query than the owner's own.
    /// </summary>
    /// <remarks>
    /// Equal similarity across owners does not reproduce starvation, and the first version of this
    /// test made that mistake: with identical embeddings the index breaks ties by internal id order,
    /// owner-000 was written first, so all four of its facts landed inside the global top-K and the
    /// rescue correctly recovered nothing. The measurement read "rescue buys 0 rows" when what it
    /// actually showed was "this owner was never starved".
    ///
    /// Crowding is about RANK, not volume. Foreign rows must outrank the owner's, which is the
    /// construction OwnerVectorStarvationIntegrationTests already uses.
    /// </remarks>
    private async Task SeedAsync()
    {
        var writer = Repository(rescue: false);

        // Foreign owners: aligned with the query, so they fill the global top-K first.
        for (var owner = 1; owner < Owners; owner++)
        {
            for (var index = 0; index < FactsPerOwner; index++)
            {
                await writer.UpsertAsync(new Fact
                {
                    FactId = $"f-{owner:D3}-{index:D2}",
                    Subject = $"subject-{owner:D3}",
                    Predicate = "likes",
                    Object = $"object-{index:D2}",
                    OwnerId = $"owner-{owner:D3}",
                    Confidence = 1.0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Embedding = [1f, 0f, 0f, 0f],
                });
            }
        }

        // The querying owner: real, live, embedded, above any sane floor — just less aligned, so it
        // loses every tie to the crowd. This is the shape the production measurement found.
        for (var index = 0; index < FactsPerOwner; index++)
        {
            await writer.UpsertAsync(new Fact
            {
                FactId = $"f-000-{index:D2}",
                Subject = "subject-000",
                Predicate = "likes",
                Object = $"object-{index:D2}",
                OwnerId = "owner-000",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [0.2f, 0.98f, 0f, 0f],
            });
        }
    }

    [Fact]
    public async Task TheRescueArmIsMeasuredAgainstTheDefaultOnIdenticalData()
    {
        await SeedAsync();

        var scope = MemoryScope.For("owner-000", includeShared: false);
        float[] query = [1f, 0f, 0f, 0f];

        // Warm both paths once so the reported cost is steady state rather than index warm-up.
        _ = await Repository(rescue: false).SearchByVectorAsync(query, Limit, 0.0, scope);
        _ = await Repository(rescue: true).SearchByVectorAsync(query, Limit, 0.0, scope);

        var offWatch = Stopwatch.StartNew();
        var off = await Repository(rescue: false).SearchByVectorAsync(query, Limit, 0.0, scope);
        offWatch.Stop();

        var onWatch = Stopwatch.StartNew();
        var on = await Repository(rescue: true).SearchByVectorAsync(query, Limit, 0.0, scope);
        onWatch.Stop();

        _output.WriteLine(
            $"SHORT-RESCUE ARM  owners={Owners} facts/owner={FactsPerOwner} limit={Limit} "
            + $"(owner-000 holds {FactsPerOwner}; corpus holds {Owners * FactsPerOwner})");
        _output.WriteLine(
            $"  rescue OFF (shipped default): yield {off.Count}/{Limit}  {offWatch.ElapsedMilliseconds} ms");
        _output.WriteLine(
            $"  rescue ON                   : yield {on.Count}/{Limit}  {onWatch.ElapsedMilliseconds} ms");
        _output.WriteLine(
            $"  recovered {on.Count - off.Count} row(s). This owner holds {FactsPerOwner}, so full "
            + "recovery reads as yield == facts/owner, not as yield == limit.");

        // VOID WITNESS. If the OFF arm already returned everything the owner holds, this owner was
        // never starved and the comparison measured nothing -- which is exactly what the first draft
        // of this test did while printing a clean-looking "recovered 0 rows".
        off.Count.Should().BeLessThan(FactsPerOwner,
            "the construction must actually starve the owner, or the arms are not comparing anything");

        // Isolation is the invariant that must hold on both arms regardless of yield.
        off.Should().OnlyContain(r => r.Fact.OwnerId == "owner-000");
        on.Should().OnlyContain(r => r.Fact.OwnerId == "owner-000");

        // The rescue keeps whichever result is larger, so it cannot lose rows. That is the only hard
        // claim worth asserting; the yields themselves are the measurement.
        on.Count.Should().BeGreaterThanOrEqualTo(off.Count,
            "the rescue keeps the larger of the indexed and scanned results, so it cannot lose rows");
    }

    /// <summary>The uncrowded control: with one owner in the index there is nothing to rescue from.</summary>
    [Fact]
    public async Task WithASingleOwnerTheRescueChangesNothing()
    {
        var writer = Repository(rescue: false);
        for (var index = 0; index < FactsPerOwner; index++)
        {
            await writer.UpsertAsync(new Fact
            {
                FactId = $"solo-{index:D2}",
                Subject = "subject-solo",
                Predicate = "likes",
                Object = $"object-{index:D2}",
                OwnerId = "owner-solo",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [1f, 0f, 0f, 0f],
            });
        }

        var scope = MemoryScope.For("owner-solo", includeShared: false);
        float[] query = [1f, 0f, 0f, 0f];

        // Warm, then time. A 4-row owner against a limit of 10 is "short" by the rescue's own gate, so
        // the scan FIRES here and finds nothing new. That is the original objection to short-rescue in
        // OwnerVectorOverFetch's remarks -- "it would tax every small tenant with an extra query on
        // every recall forever" -- and the counter-argument is that an owner-bounded scan is cheap
        // precisely because the owner is small. Neither had ever been measured, so this times it.
        _ = await Repository(rescue: false).SearchByVectorAsync(query, Limit, 0.0, scope);
        _ = await Repository(rescue: true).SearchByVectorAsync(query, Limit, 0.0, scope);

        var offWatch = Stopwatch.StartNew();
        var off = await Repository(rescue: false).SearchByVectorAsync(query, Limit, 0.0, scope);
        offWatch.Stop();

        var onWatch = Stopwatch.StartNew();
        var on = await Repository(rescue: true).SearchByVectorAsync(query, Limit, 0.0, scope);
        onWatch.Stop();

        _output.WriteLine(
            $"SINGLE-OWNER CONTROL (the small-tenant tax): owner holds {FactsPerOwner}, limit {Limit}");
        _output.WriteLine($"  rescue OFF: yield {off.Count}  {offWatch.ElapsedMilliseconds} ms");
        _output.WriteLine(
            $"  rescue ON : yield {on.Count}  {onWatch.ElapsedMilliseconds} ms  "
            + "(the scan fires and recovers nothing; this is what a small tenant pays)");

        // The control that says a short result is not automatically starvation: this owner is short
        // because it is small, and the rescue correctly adds nothing.
        off.Count.Should().Be(FactsPerOwner);
        on.Count.Should().Be(FactsPerOwner);
    }
}
