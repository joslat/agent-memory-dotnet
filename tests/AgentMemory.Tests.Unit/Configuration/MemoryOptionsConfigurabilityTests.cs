using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

// Deliberately NOT namespace ...Tests.Unit.Options: that name shadows Microsoft.Extensions.Options
// and breaks `Options.Create(...)` in every other test file in the assembly.
namespace AgentMemory.Tests.Unit.Configuration;

/// <summary>
/// 25.1. The <c>Action&lt;MemoryOptions&gt;</c> registration overload must be able to configure memory.
/// </summary>
/// <remarks>
/// <para>
/// It could not. All 19 properties on <see cref="MemoryOptions"/> were <c>init</c>-only, so a configure
/// lambda — the idiomatic .NET options pattern, and the overload most consumers reach for first —
/// could assign none of them. The only reachable group was <c>Extraction</c>, which happens to be a
/// mutable class rather than a record.
/// </para>
/// <para>
/// The failure was a compile error rather than silent misbehaviour, which is the one merciful thing
/// about it, but the consequence was still that the documented configuration entry point configured
/// nothing and the docs told people to avoid it.
/// </para>
/// </remarks>
public sealed class MemoryOptionsConfigurabilityTests
{
    [Fact]
    public void EveryScalarOptionIsSettableFromAConfigureLambda()
    {
        // Red before 25.1: every line in this lambda was a compile error (CS8852, "init-only property
        // can only be assigned in an object initializer"). That is the whole defect, expressed as the
        // code a consumer would naturally write.
        var options = new MemoryOptions();

        void Configure(MemoryOptions memory)
        {
            memory.EnableGraphRag = true;
            memory.RescueShortOwnerResults = true;
            memory.NodeDistanceReranking = true;
            memory.MentionFrequencyReranking = true;
            memory.DeferAccessTracking = true;
            memory.ConfidenceReinforcementAlpha = 0.25;
            memory.ResolveTemporalQueries = true;
            memory.TemporalQueryClocks = TemporalQueryClocks.ValidAndTransactionTime;
            memory.OmitEmbeddingsFromRecall = true;
            memory.SkipEscalationWhenOwnerHasNoRows = true;
        }

        Configure(options);

        options.EnableGraphRag.Should().BeTrue();
        options.RescueShortOwnerResults.Should().BeTrue();
        options.NodeDistanceReranking.Should().BeTrue();
        options.MentionFrequencyReranking.Should().BeTrue();
        options.DeferAccessTracking.Should().BeTrue();
        options.ConfidenceReinforcementAlpha.Should().Be(0.25);
        options.ResolveTemporalQueries.Should().BeTrue();
        options.TemporalQueryClocks.Should().Be(TemporalQueryClocks.ValidAndTransactionTime);
        options.OmitEmbeddingsFromRecall.Should().BeTrue();
        options.SkipEscalationWhenOwnerHasNoRows.Should().BeTrue();
    }

    [Fact]
    public void ConfiguringThroughTheContainerReachesTheResolvedOptions()
    {
        // The property being settable is necessary but not sufficient — the lambda must also actually
        // run against the instance the container hands out. Registration and effect are separate
        // failures, and this project has shipped the first without the second more than once.
        var services = new ServiceCollection();
        services.AddAgentMemoryCore(memory =>
        {
            memory.NodeDistanceReranking = true;
            memory.ConfidenceReinforcementAlpha = 0.5;
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;

        resolved.NodeDistanceReranking.Should().BeTrue();
        resolved.ConfidenceReinforcementAlpha.Should().Be(0.5);
    }

    [Fact]
    public void NestedOptionObjectsStayInitOnlySoSharedDefaultsCannotBeMutated()
    {
        // THE reason the split exists, pinned so nobody "finishes the job" by making these settable.
        // RecallOptions.Default and friends are ONE static instance for the whole process. If their
        // properties were settable, `options.Recall.MaxFacts = 5` inside one consumer's configure
        // lambda would silently change the default for every other consumer in the application.
        //
        // Reflected rather than listed, so a nested option added later is covered automatically.
        var nested = typeof(MemoryOptions).GetProperties()
            .Where(property => property.PropertyType.Namespace == typeof(MemoryOptions).Namespace)
            .ToList();

        nested.Should().NotBeEmpty();

        // Only the ones defaulting to a SHARED STATIC singleton carry the hazard. Types defaulting to
        // `new()` get a fresh instance per MemoryOptions, so mutating them harms nobody else.
        var sharedDefault = new[]
        {
            nameof(MemoryOptions.Recall), nameof(MemoryOptions.ContextBudget),
            nameof(MemoryOptions.MemoryDecay), nameof(MemoryOptions.Ranking),
        };

        nested.Where(property => sharedDefault.Contains(property.Name, StringComparer.Ordinal))
            .Should().OnlyContain(
                property => property.SetMethod!.ReturnParameter
                    .GetRequiredCustomModifiers()
                    .Any(modifier => modifier.Name == "IsExternalInit"),
                "a settable nested option would let one consumer mutate a process-wide shared default");
    }

    [Fact]
    public void TheSharedDefaultsAreGenuinelyShared()
    {
        // Establishes the premise of the test above rather than assuming it. If these ever stop being
        // singletons, the init-only restriction on nested options can be revisited.
        ReferenceEquals(RecallOptions.Default, RecallOptions.Default).Should().BeTrue();
        ReferenceEquals(new MemoryOptions().Recall, new MemoryOptions().Recall).Should().BeTrue(
            "two MemoryOptions instances share one RecallOptions.Default instance");
    }
}
