using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// J2.2. Expansion makes one relation complete, but it can only expand predicates that similarity
/// already surfaced in the top-K. A question naming several relations therefore reaches only whichever
/// of them retrieval happened to nominate — the measured cause of the surviving `gpt4_15e38248`
/// failure, which asks about buy, assemble, sell and fix. These cover supplying the relations from the
/// question instead.
/// </summary>
public sealed class FactExpansionQuestionRelationTests
{
    private readonly IFactRepository _factRepo = Substitute.For<IFactRepository>();

    public FactExpansionQuestionRelationTests()
    {
        _factRepo
            .SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact Fact, double Score)>>(
                [(Fact("f-1", "bought"), 0.9)]));
        _factRepo
            .SearchByCanonicalPredicatesAsync(
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
                Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));
    }

    [Fact]
    public async Task RelationsNamedByTheQuestionAreExpandedEvenWhenSimilarityNeverSurfacedThem()
    {
        // Only "bought" is in the top-K. The other three relations the question names must still be
        // expanded, or three quarters of the answer is unreachable by construction.
        await SearchAsync(["bought", "assembled", "sold", "fixed"]).ConfigureAwait(true);

        var predicates = CapturedPredicates();
        predicates.Should().Contain("bought")
            .And.Contain("assembled").And.Contain("sold").And.Contain("fixed");
    }

    [Fact]
    public async Task EveryStoredFormOfANamedRelationIsExpanded()
    {
        // The write-side canonicalizer never folds morphology, so one relation is stored under several
        // keys - "planned" holds 839 facts in the measured graph and "plans" holds 14. Expanding the
        // canonical name alone would silently miss the smaller bucket.
        await SearchAsync(["planned"]).ConfigureAwait(true);

        var predicates = CapturedPredicates();
        predicates.Should().Contain("planned").And.Contain("plans");
    }

    [Fact]
    public async Task WithNoQuestionRelationsTheExpandedPredicatesAreExactlyTodaysTopKDerivedSet()
    {
        // The fallback that makes this incapable of being worse than current behaviour.
        await SearchAsync([]).ConfigureAwait(true);

        CapturedPredicates().Should().Equal("bought");
    }

    [Fact]
    public async Task QuestionRelationsAreIgnoredWhenExpansionIsDisabled()
    {
        var service = CreateSut();

        await service.SearchFactsAsync(
                new float[8], 10, 0, null, false, 100, ["assembled"], CancellationToken.None)
            .ConfigureAwait(true);

        await _factRepo.DidNotReceive().SearchByCanonicalPredicatesAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<MemoryScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANamedRelationIsExpandedEvenWhenTopKIsEmpty()
    {
        // Today expansion returns early on an empty top-K because it has nothing to derive predicates
        // from. A question that names its relations does not have that problem, and returning nothing
        // when the relation was stated outright would be the same completeness failure again.
        _factRepo
            .SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact Fact, double Score)>>([]));

        await SearchAsync(["assembled"]).ConfigureAwait(true);

        CapturedPredicates().Should().Contain("assembled");
    }

    [Fact]
    public async Task ThePredicateSetIsDeduplicated()
    {
        // "bought" arrives from both the top-K and the question; querying it twice would waste the
        // expansion limit on a duplicate.
        await SearchAsync(["bought"]).ConfigureAwait(true);

        var predicates = CapturedPredicates();
        predicates.Should().OnlyHaveUniqueItems();
    }

    private async Task SearchAsync(string[] questionRelations)
    {
        var service = CreateSut();
        await service.SearchFactsAsync(
                new float[8], 10, 0, null, true, 100, questionRelations, CancellationToken.None)
            .ConfigureAwait(true);
    }

    private IReadOnlyList<string> CapturedPredicates() =>
        (IReadOnlyList<string>)_factRepo.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(
                IFactRepository.SearchByCanonicalPredicatesAsync))
            .GetArguments()[0]!;

    private LongTermMemoryService CreateSut() =>
        new(Substitute.For<IEntityRepository>(),
            _factRepo,
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

    private static Fact Fact(string id, string predicate) => new()
    {
        FactId = id,
        Subject = "user",
        Predicate = predicate,
        Object = "a sofa",
        Confidence = 1.0,
        CreatedAtUtc = DateTimeOffset.UnixEpoch
    };
}
