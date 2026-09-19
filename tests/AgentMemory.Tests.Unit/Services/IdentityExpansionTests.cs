using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// E-1. Recall crosses a declared identity — on BOTH paths, or it is not a retrieval semantic.
/// </summary>
/// <remarks>
/// <para>
/// Similarity cannot cross an alias: "deliveries at the new flat" and "a delivery at the place on
/// Ferrow Row" share no text to be similar on. Three measured arms proved the edge alone does nothing
/// and that re-ranking over it actively harms, because re-ranking reorders what retrieval returned and
/// the missing fact was never a candidate.
/// </para>
/// <para>
/// The live/as-of pairing is asserted deliberately. A semantic wired into one path and not its twin is
/// the defect shape this codebase has paid for repeatedly — most recently firing, which de-duplicated
/// on the live path and not the point-in-time one.
/// </para>
/// </remarks>
public sealed class IdentityExpansionTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The whole point: a fact filed under the other name reaches the caller.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFactFiledUnderTheOtherNameIsRecalled(bool asOf)
    {
        var harness = new Harness
        {
            Retrieved = [MakeFact("alias-declaration"), MakeFact("delivery-1")],
            Expanded = [MakeFact("delivery-2-under-the-other-name")],
        };

        var context = await harness.RecallAsync(expandByIdentity: true, asOf: asOf);

        context.RelevantFacts.Items.Select(f => f.FactId)
            .Should().Contain("delivery-2-under-the-other-name",
                "the expansion hop is what makes the aliased fact a candidate at all");
    }

    /// <summary>Off by default: no expansion call is made at all.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheHopIsNotTakenWhenTheFeatureIsOff(bool asOf)
    {
        var harness = new Harness { Retrieved = [MakeFact("a")], Expanded = [MakeFact("b")] };

        var context = await harness.RecallAsync(expandByIdentity: false, asOf: asOf);

        context.RelevantFacts.Items.Select(f => f.FactId).Should().BeEquivalentTo(["a"]);
        await harness.LongTerm.DidNotReceive().GetFactsSharingAliasedEntitiesAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The retrieved facts are the seeds, and the cap travels with them.</summary>
    [Fact]
    public async Task TheRetrievedFactsAreTheSeedsAndTheCapIsHonoured()
    {
        var harness = new Harness { Retrieved = [MakeFact("seed-1"), MakeFact("seed-2")] };

        await harness.RecallAsync(expandByIdentity: true, asOf: false, maxIdentityExpanded: 7);

        await harness.LongTerm.Received(1).GetFactsSharingAliasedEntitiesAsync(
            Arg.Is<IReadOnlyList<string>>(ids => ids.Contains("seed-1") && ids.Contains("seed-2")),
            7, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A fact returned by BOTH similarity and the hop appears once.
    /// </summary>
    /// <remarks>
    /// Counting questions are what this feature is measured on, so a duplicate is not cosmetic — it is
    /// an over-count, the exact signature the Goodhart guard watches for.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFactReturnedByBothRoutesAppearsOnce(bool asOf)
    {
        var harness = new Harness
        {
            Retrieved = [MakeFact("shared")],
            Expanded = [MakeFact("shared")],
        };

        var context = await harness.RecallAsync(expandByIdentity: true, asOf: asOf);

        context.RelevantFacts.Items.Count(f => f.FactId == "shared").Should().Be(1);
    }

    /// <summary>No retrieved facts means no hop: there is nothing to expand FROM.</summary>
    [Fact]
    public async Task AnEmptyRetrievalTakesNoHop()
    {
        var harness = new Harness { Retrieved = [] };

        await harness.RecallAsync(expandByIdentity: true, asOf: false);

        await harness.LongTerm.DidNotReceive().GetFactsSharingAliasedEntitiesAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(),
            Arg.Any<CancellationToken>());
    }

    private static Fact MakeFact(string id) => new()
    {
        FactId = id,
        Subject = id,
        Predicate = "took_delivery_at",
        Object = "somewhere",
        Confidence = 1.0,
        CreatedAtUtc = AsOf,
    };

    private sealed class Harness
    {
        internal ILongTermMemoryService LongTerm { get; } = Substitute.For<ILongTermMemoryService>();

        internal IReadOnlyList<Fact> Retrieved { get; init; } = [];

        internal IReadOnlyList<Fact> Expanded { get; init; } = [];

        internal Task<MemoryContext> RecallAsync(
            bool expandByIdentity, bool asOf, int maxIdentityExpanded = 50)
        {
            LongTerm.SearchFactsAsync(
                    Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Retrieved));
            LongTerm.SearchFactsAsOfAsync(
                    Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                    Arg.Any<MemoryScope?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Retrieved));
            LongTerm.GetFactsSharingAliasedEntitiesAsync(
                    Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Expanded));

            var shortTerm = Substitute.For<IShortTermMemoryService>();
            shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([]);
            shortTerm.GetRecentMessagesAsOfAsync(
                    Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns([]);

            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new float[8]);

            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(AsOf);

            var options = new MemoryOptions();
            var assembler = new MemoryContextAssembler(
                shortTerm,
                LongTerm,
                Substitute.For<IReasoningMemoryService>(),
                graphRag: null,
                embeddings,
                clock,
                Options.Create(options),
                NullLogger<MemoryContextAssembler>.Instance,
                new DefaultMemoryIsolationPolicy(
                    Options.Create(options.Isolation),
                    NullLogger<DefaultMemoryIsolationPolicy>.Instance));

            var request = new RecallRequest
            {
                SessionId = "session-1",
                UserId = "owner-1",
                Query = "how many deliveries at the new flat?",
                Options = RecallOptions.Default with
                {
                    ExpandFactsByIdentity = expandByIdentity,
                    MaxIdentityExpandedFacts = maxIdentityExpanded,
                    MaxFacts = 10,
                    MaxEntities = 0,
                    MaxPreferences = 0,
                    MaxRecentMessages = 0,
                    MaxTraces = 0,
                },
            };

            return asOf
                ? assembler.AssembleContextAsOfAsync(request, AsOf, AsOf, CancellationToken.None)
                : assembler.AssembleContextAsync(request, CancellationToken.None);
        }
    }
}
