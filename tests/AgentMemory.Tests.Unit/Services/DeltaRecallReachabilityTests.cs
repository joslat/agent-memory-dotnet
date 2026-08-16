using System.Reflection;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.5 step 9. Delta recall is reachable end to end, and the default-interface members are not
/// silently throwing in production.
/// </summary>
/// <remarks>
/// <para>
/// This project has now found <b>fifteen</b> separate instances of a feature that shipped complete and
/// unreachable: procedural promotion that no consumer could call, two rerankers with full test suites
/// and zero registrations, options that bound and validated and did nothing. Delta recall is built the
/// way that keeps happening — new members added as throwing default implementations so the interface
/// stays SemVer-safe — which means the shipped implementation not overriding one of them would be
/// invisible until a host hit the throw at runtime.
/// </para>
/// <para>
/// So it is reflected, not listed. A new repository implementation that forgets an override fails here
/// rather than in someone's production log.
/// </para>
/// </remarks>
public sealed class DeltaRecallReachabilityTests
{
    /// <summary>
    /// Implementations that legitimately inherit the throwing default, each with its reason.
    /// </summary>
    /// <remarks>
    /// Deliberately explicit. An empty allowlist would be nicer, but a silently-growing one is exactly
    /// the shape of the defect this test exists to catch — so every entry has to be argued for in
    /// writing, here, where the next reader sees it.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Allowed =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // NAMS is a hosted backend whose API has no change-window query. Overriding the member to
            // fabricate one from repeated full reads would be the "diff of two full recalls" alternative
            // this design rejected on determinism grounds.
            ["NamsFactRepository"] = "hosted backend exposes no clock-window query",
            ["NamsPreferenceRepository"] = "hosted backend exposes no clock-window query",
            ["NamsEntityRepository"] = "hosted backend exposes no clock-window query",
            ["NamsMemoryService"] = "hosted backend exposes no clock-window query",
        };

    private static IEnumerable<Assembly> ShippedAssemblies() =>
    [
        typeof(AgentMemory.Core.Services.MemoryService).Assembly,
        typeof(AgentMemory.Neo4j.Repositories.Neo4jFactRepository).Assembly,
    ];

    private static IReadOnlyList<Type> ImplementationsOf(Type contract) =>
        [.. ShippedAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && !type.IsInterface && type.IsAssignableTo(contract))];

    /// <summary>
    /// True when <paramref name="type"/> supplies its own body for <paramref name="method"/> rather
    /// than inheriting the interface's throwing default.
    /// </summary>
    private static bool DeclaresOwnImplementation(Type type, Type contract, string method)
    {
        var map = type.GetInterfaceMap(contract);
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == method);
        index.Should().BeGreaterThanOrEqualTo(0, "{0} must declare {1}", contract.Name, method);

        // A default interface member maps back to the interface itself; a real override maps to the
        // implementing type. This is the whole check.
        return map.TargetMethods[index].DeclaringType != contract;
    }

    public static TheoryData<string, string> NewMembers() => new()
    {
        { nameof(IFactRepository), "ListChangedInWindowAsync" },
        { nameof(IPreferenceRepository), "ListChangedInWindowAsync" },
        { nameof(IEntityRepository), "ListCreatedInWindowAsync" },
        { nameof(IMemoryRecall), "RecallChangedSinceAsync" },
    };

    [Theory]
    [MemberData(nameof(NewMembers))]
    public void EveryShippedImplementationEitherOverridesTheNewMemberOrIsAllowlisted(
        string contractName, string methodName)
    {
        var contract = typeof(IFactRepository).Assembly.GetTypes()
            .Single(type => type.IsInterface && type.Name == contractName);

        var implementations = ImplementationsOf(contract);
        implementations.Should().NotBeEmpty("the contract must have at least one shipped implementation");

        var inheritingTheThrow = implementations
            .Where(type => !DeclaresOwnImplementation(type, contract, methodName))
            .Select(type => type.Name)
            .Where(name => !Allowed.ContainsKey(name))
            .ToList();

        inheritingTheThrow.Should().BeEmpty(
            "a shipped {0} that inherits the throwing default for {1} is the sixteenth "
            + "shipped-but-unreachable feature", contractName, methodName);
    }

    [Fact]
    public void TheContainerHandsOutAServiceThatCanActuallyAnswerADelta()
    {
        // Reflection proves the method exists; this proves the object the container gives a host is the
        // one that has it. Both have failed independently before.
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

        var recall = scope.ServiceProvider.GetRequiredService<IMemoryRecall>();

        DeclaresOwnImplementation(recall.GetType(), typeof(IMemoryRecall), "RecallChangedSinceAsync")
            .Should().BeTrue();
    }

    [Fact]
    public async Task TheDefaultImplementationThrowsRatherThanReturningAnEmptyDelta()
    {
        // A backend that cannot answer must SAY so. Returning an empty MemoryDelta would tell the agent
        // "nothing changed while you were away" -- a confident, wrong answer, which is worse than an
        // error precisely because it is actionable.
        IMemoryRecall stub = new RecallWithoutDeltaSupport();

        var act = async () => await stub.RecallChangedSinceAsync(
            new MemoryDeltaRequest { Since = DateTimeOffset.UnixEpoch });

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    /// <summary>A minimal implementation of the pre-30.5 surface, to prove the DIMs kept it compiling.</summary>
    private sealed class RecallWithoutDeltaSupport : IMemoryRecall
    {
        public Task<RecallResult> RecallAsync(
            RecallRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RecallResult> RecallAsOfAsync(
            RecallRequest request, DateTimeOffset asOf, DateTimeOffset? knownAs = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
