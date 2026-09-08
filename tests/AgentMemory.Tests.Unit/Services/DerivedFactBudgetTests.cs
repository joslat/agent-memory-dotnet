using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// W2: the READ side of derived memory — three states, and null is the measured-harmful one.
/// </summary>
/// <remarks>
/// <para>
/// The session accountant writes counts and sums and marks them with <c>derivation_key</c>, and
/// <b>nothing at recall read that mark</b>. Its output therefore entered the same similarity pool as
/// everything else. Measured on the arithmetic vertical: <b>30% to 14%</b>, with facts-per-question
/// falling 35.4 to 27.6 as ~2,560 derived facts displaced the source values they were computed from.
/// A feature that writes answers into memory made the inputs harder to retrieve.
/// </para>
/// <para>
/// The states must stay distinguishable at the repository seam, because that is where the difference
/// actually lands. Asserting on returned facts alone would pass for an implementation that queried
/// the same way and filtered afterwards — which would keep the dilution it exists to remove.
/// </para>
/// </remarks>
public sealed class DerivedFactBudgetTests
{
    [Fact]
    public async Task NullTakesThePreExistingOverload_SoSealedMeasurementsStayComparable()
    {
        var (service, repo) = Create();

        await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: null, CancellationToken.None);

        await repo.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
            Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
            Arg.Any<DerivedFactMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ZeroExcludesDerivedFactsFromTheSearchItself()
    {
        var (service, repo) = Create();

        await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: 0, CancellationToken.None);

        await repo.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
            DerivedFactMode.Exclude, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Zero must NOT issue a second round trip: excluding is already done by the main query's filter.
    /// </summary>
    [Fact]
    public async Task ZeroDoesNotPayForASecondSearchThatWouldReturnNothing()
    {
        var (service, repo) = Create();

        await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: 0, CancellationToken.None);

        await repo.DidNotReceive().SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
            DerivedFactMode.Only, Arg.Any<CancellationToken>());
    }

    /// <summary>The state the accountant can finally be measured in: its own budget.</summary>
    [Fact]
    public async Task APositiveBudgetFetchesDerivedFactsSeparatelyAndAppendsThem()
    {
        var (service, repo) = Create();
        repo.SearchByVectorAsync(
                Arg.Any<float[]>(), 3, Arg.Any<double>(), Arg.Any<MemoryScope>(),
                DerivedFactMode.Only, Arg.Any<CancellationToken>())
            .Returns([(Fact("derived-1"), 0.9)]);

        var facts = await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: 3, CancellationToken.None);

        facts.Select(f => f.FactId).Should().Contain("derived-1");
        await repo.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), 3, Arg.Any<double>(), Arg.Any<MemoryScope>(),
            DerivedFactMode.Only, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The budget is SEPARATE: the source search still asks for its full <c>limit</c>.
    /// </summary>
    /// <remarks>
    /// This is the whole point. If the derived budget came out of <c>MaxFacts</c>, the feature would
    /// reproduce the displacement it exists to remove — quietly, and with a knob that looked like a fix.
    /// </remarks>
    [Fact]
    public async Task TheDerivedBudgetDoesNotComeOutOfTheSourceFactBudget()
    {
        var (service, repo) = Create();

        await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: 3, CancellationToken.None);

        await repo.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), 10, Arg.Any<double>(), Arg.Any<MemoryScope>(),
            DerivedFactMode.Exclude, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADerivedFactAlreadyPresentIsNotDuplicated()
    {
        var (service, repo) = Create();
        repo.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
                DerivedFactMode.Exclude, Arg.Any<CancellationToken>())
            .Returns([(Fact("shared"), 0.9)]);
        repo.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
                DerivedFactMode.Only, Arg.Any<CancellationToken>())
            .Returns([(Fact("shared"), 0.9)]);

        var facts = await service.SearchFactsAsync(
            new float[8], 10, 0.0, MemoryScope.Global,
            expandByPredicate: false, expansionLimit: 0, questionRelations: [],
            maxDerivedFacts: 3, CancellationToken.None);

        facts.Count(f => f.FactId == "shared").Should().Be(1);
    }

    private static Fact Fact(string id) => new()
    {
        FactId = id, Subject = "s", Predicate = "p", Object = "o", Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static (LongTermMemoryService Service, IFactRepository Repo) Create()
    {
        var repo = Substitute.For<IFactRepository>();
        repo.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
                Arg.Any<CancellationToken>())
            .Returns([]);
        repo.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope>(),
                Arg.Any<DerivedFactMode>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var isolation = Substitute.For<IMemoryIsolationPolicy>();
        isolation.ResolveReadScope(
                Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.Global);

        var service = new LongTermMemoryService(
            Substitute.For<IEntityRepository>(),
            repo,
            Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            isolation);
        return (service, repo);
    }
}
