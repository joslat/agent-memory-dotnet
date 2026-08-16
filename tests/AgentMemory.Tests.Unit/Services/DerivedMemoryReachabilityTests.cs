using System.Reflection;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core;
using AgentMemory.Core.Extraction.Derivation;
using AgentMemory.Neo4j.Derivation;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.6 step 9. Every operator flag reaches an evaluator, and the accountant reaches the container.
/// </summary>
/// <remarks>
/// <para>
/// This is the sixteenth shipped-but-unreachable instance not happening. An arithmetic feature wears
/// that defect especially well: the symptom of an operator flag nobody reads is simply that certain
/// aggregates never appear, which looks exactly like "the data did not support one".
/// </para>
/// <para>
/// Reflected in both directions — every flag has an evaluator, and every evaluator has a flag — because
/// the two failures are different. An orphan flag is a dead option; an orphan evaluator is dead code
/// that no configuration can ever run.
/// </para>
/// </remarks>
public sealed class DerivedMemoryReachabilityTests
{
    private static IReadOnlyList<DerivationOperators> AllFlags() =>
        [.. Enum.GetValues<DerivationOperators>().Where(value => value != DerivationOperators.None)];

    [Fact]
    public void EveryOperatorFlagHasExactlyOneEvaluator()
    {
        var covered = SessionAccountant.AllEvaluators.Select(e => e.Operator).ToList();

        covered.Should().OnlyHaveUniqueItems("two evaluators claiming one flag would both run");
        covered.Should().BeEquivalentTo(AllFlags(),
            "an operator flag a host can set and no code reads is a dead option");
    }

    [Fact]
    public void EveryEvaluatorInTheAssemblyIsInTheAccountantsList()
    {
        // The other direction: an evaluator class written, unit-tested, and never added to the list is
        // dead code no configuration can run -- the reranker defect, exactly.
        var implemented = typeof(IDerivationEvaluator).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface
                           && type.IsAssignableTo(typeof(IDerivationEvaluator)))
            .ToList();

        implemented.Should().NotBeEmpty();
        SessionAccountant.AllEvaluators.Select(e => e.GetType())
            .Should().BeEquivalentTo(implemented);
    }

    [Fact]
    public void EveryOperatorHasItsOwnDerivedPredicateSpelling()
    {
        // A shared spelling would make two aggregates over one group collide on derivation_key and
        // overwrite each other in place -- silently, since the upsert is a MERGE.
        var spellings = AllFlags()
            .Select(op => DerivedPredicates.For(op, "p"))
            .ToList();

        spellings.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheAccountantIsResolvableFromTheProductionContainer()
    {
        // Reflection proves the class exists; this proves the container hands one out. Both have
        // failed independently in this codebase.
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>())
            .AddSingleton(Substitute.For<IConversationRepository>())
            .AddSingleton(Substitute.For<IEntityRepository>())
            .AddSingleton(Substitute.For<IPreferenceRepository>())
            .AddSingleton(Substitute.For<IReasoningTraceRepository>())
            .AddSingleton(Substitute.For<IReasoningStepRepository>())
            .AddSingleton(Substitute.For<IToolCallRepository>())
            .AddSingleton(Substitute.For<IRelationshipRepository>())
            .AddSingleton(Substitute.For<
                Microsoft.Extensions.AI.IEmbeddingGenerator<
                    string, Microsoft.Extensions.AI.Embedding<float>>>())
            .AddAgentMemoryCore(_ => { })
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IDerivedMemoryAccountant>().Should().NotBeNull();
    }

    [Fact]
    public void TheRegistrationIsUnconditionalSoIOptionsReconfigurationWorks()
    {
        // Registered with the flag DEFAULT (off) and still resolvable. A registration gated on the
        // option reads it once, at container-build time, so a host that enables derived memory through
        // IOptions afterwards would find the service simply absent and the feature silently off -- the
        // reranker pattern this codebase adopted precisely to avoid that.
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>());
        services.AddAgentMemoryCore(_ => { });

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IDerivedMemoryAccountant));
    }

    [Fact]
    public void ThePipelineAcceptsTheAccountant()
    {
        // The link that would otherwise be missing: the accountant can exist, be registered, be
        // resolvable, and never be called. This asserts the constructor parameter that carries it.
        var pipeline = typeof(AgentMemory.Core.ServiceCollectionExtensions).Assembly
            .GetType("AgentMemory.Core.Services.MemoryExtractionPipeline")!;

        pipeline.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(ctor => ctor.GetParameters())
            .Should().Contain(parameter => parameter.ParameterType == typeof(IDerivedMemoryAccountant));
    }

    [Fact]
    public void TheNeo4jRepositoryOverridesBothNewMembers()
    {
        // Both ship as default interface methods for SemVer reasons; one throws and one returns empty,
        // so a missing override would surface either as a runtime failure or -- worse -- as an
        // accountant that quietly finds nothing.
        var repository = typeof(AgentMemory.Neo4j.Repositories.Neo4jFactRepository);
        var map = repository.GetInterfaceMap(typeof(IFactRepository));

        foreach (var name in new[] { "GetGroupFactsAsync", "UpsertDerivedAsync" })
        {
            var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == name);
            index.Should().BeGreaterThanOrEqualTo(0);
            map.TargetMethods[index].DeclaringType.Should().Be(repository,
                "{0} must be implemented, not inherited from the interface default", name);
        }
    }

    [Fact]
    public void TheDerivationKeyExcludesTheObjectSoRecomputeUpdatesInPlace()
    {
        // If the value were part of the identity, every recompute would create a NEW node and the graph
        // would accumulate one dead aggregate per observation -- the single most consequential line of
        // this feature's schema design, and invisible until a corpus grows.
        var first = DerivationKey.For("user", "count_of:x", "Count", "alice");
        var second = DerivationKey.For("user", "count_of:x", "Count", "alice");

        first.Should().Be(second);
    }

    [Theory]
    [InlineData("user", "count_of:x", "Count", "bob")]
    [InlineData("user", "count_of:x", "Delta", "alice")]
    [InlineData("user", "count_of:y", "Count", "alice")]
    [InlineData("other", "count_of:x", "Count", "alice")]
    public void EveryComponentOfTheDerivationKeyChangesIt(
        string subject, string predicate, string op, string owner)
    {
        // Each component earns its place: two owners' aggregates must not collide, and neither must two
        // operators over one group.
        var baseline = DerivationKey.For("user", "count_of:x", "Count", "alice");

        DerivationKey.For(subject, predicate, op, owner).Should().NotBe(baseline);
    }

    [Fact]
    public void ASharedOwnerAndTheLiteralSharedMarkerDoNotCollide()
    {
        // A tenant literally named "__shared__" must not share an identity with genuinely shared
        // aggregates. Stated as a test because the marker is a magic string.
        DerivationKey.For("user", "p", "Count", ownerId: null)
            .Should().NotBe(DerivationKey.For("user", "p", "Count", "__shared__owner"));
    }
}
