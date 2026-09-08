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
/// W1c. Predicate expansion inside point-in-time recall, and the two-clock invariant that IS the
/// feature.
/// </summary>
/// <remarks>
/// <para>
/// <c>RecallAsOfAsync</c> reached <c>SearchFactsAsOfAsync</c>, which had no expansion parameters at
/// all — so a caller who asked for aggregation on a point-in-time question got top-K similarity and
/// no expansion, silently. The benchmark harness refused to run rather than let those flags do
/// nothing; this is the other half of that refusal, made true.
/// </para>
/// <para>
/// <b>The invariant is the point, not the feature.</b> Expansion widens a query from "the K most
/// similar facts" to "this relation, whole" — and a relation returned whole from an as-of query must
/// be the relation <i>as it stood at that instant</i>. An expanded hop that drops the clocks leaks
/// facts from outside the window into an answer that claims to be point-in-time, and a wrong
/// point-in-time answer is indistinguishable from a right one without checking the clock.
/// </para>
/// <para>
/// Written RED-FIRST and observed failing before the implementation existed, so it is shaped by the
/// invariant rather than by the code that satisfies it.
/// </para>
/// </remarks>
public sealed class AsOfExpansionTests
{
    private static readonly DateTimeOffset ValidAsOf = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SystemAsOf = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpansionReachesThePointInTimeFactSearch()
    {
        var (assembler, longTerm) = Create();

        await assembler.AssembleContextAsOfAsync(
            Request(expand: true), ValidAsOf, SystemAsOf, CancellationToken.None);

        await longTerm.Received(1).SearchFactsAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope>(), Arg.Any<DateTimeOffset?>(),
            expandByPredicate: true, Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The invariant: an expanded hop must be bounded by BOTH clocks.</summary>
    [Fact]
    public async Task TheExpandedSearchCarriesBothClocks()
    {
        var (assembler, longTerm) = Create();

        await assembler.AssembleContextAsOfAsync(
            Request(expand: true), ValidAsOf, SystemAsOf, CancellationToken.None);

        await longTerm.Received(1).SearchFactsAsOfAsync(
            Arg.Any<float[]>(),
            ValidAsOf,
            Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
            SystemAsOf,
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Off must remain byte-identical: the expansion overload is not called at all.
    /// </summary>
    /// <remarks>
    /// Every sealed measurement in this repository was taken on the unexpanded as-of path. If the
    /// wide overload were called with <c>expandByPredicate: false</c> instead of not being called,
    /// an implementor overriding only the narrow one would silently stop being consulted.
    /// </remarks>
    [Fact]
    public async Task TheOffStateDoesNotTakeTheExpansionOverload()
    {
        var (assembler, longTerm) = Create();

        await assembler.AssembleContextAsOfAsync(
            Request(expand: false), ValidAsOf, SystemAsOf, CancellationToken.None);

        await longTerm.DidNotReceive().SearchFactsAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
        await longTerm.Received(1).SearchFactsAsOfAsync(
            Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    private static RecallRequest Request(bool expand) => new()
    {
        SessionId = "s1",
        UserId = "u1",
        Query = "how much did I pay in total",
        QueryEmbedding = new float[8],
        Options = new RecallOptions
        {
            MaxFacts = 10,
            ExpandFactsByPredicate = expand,
            MaxExpandedFacts = 60,
        }
    };

    private static (MemoryContextAssembler Assembler, ILongTermMemoryService LongTerm) Create()
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchFactsAsOfAsync(
                Arg.Any<float[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.GetRecentMessagesAsOfAsync(
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[8]);

        var options = new MemoryOptions();
        var assembler = new MemoryContextAssembler(
            shortTerm,
            longTerm,
            Substitute.For<IReasoningMemoryService>(),
            graphRag: null,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(options),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(options.Isolation),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        return (assembler, longTerm);
    }
}
