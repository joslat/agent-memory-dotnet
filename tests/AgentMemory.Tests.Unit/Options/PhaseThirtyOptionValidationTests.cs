using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

// OptionsTests, not Options: a namespace segment named Options shadows Microsoft.Extensions.Options.
namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// Every numeric option Phase 30 added is validated at startup.
/// </summary>
/// <remarks>
/// <para>
/// This class exists because the same gap has now been found <b>twice in one phase</b>. Wave B's
/// self-review found six new numeric options shipped with no validation; Wave C then added eight more
/// the same way, and an end-of-phase review found those the same way. The pattern is not carelessness
/// about one option — it is that adding an option and validating it are separate acts, and only the
/// first is required to make a feature work.
/// </para>
/// <para>
/// Every one of these misconfigures <b>silently</b>. A zero budget makes a feature look broken rather
/// than misconfigured; a non-positive window inverts a comparison; a confidence outside [0,1]
/// propagates into every ranking and dedup computation that reads it. None of them throw, which is
/// exactly why the validator has to.
/// </para>
/// </remarks>
public sealed class PhaseThirtyOptionValidationTests
{
    private static IServiceCollection CoreServices() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>());

    /// <summary>
    /// Builds a container from a fully-constructed options instance and resolves the options.
    /// </summary>
    /// <remarks>
    /// The <b>instance</b> overload, not the lambda one, and that is forced rather than chosen:
    /// <c>MemoryOptions.Recall</c> is init-only, so <c>o.Recall = …</c> inside an
    /// <c>Action&lt;MemoryOptions&gt;</c> does not compile — the documented limitation that gave the
    /// instance overload its reason to exist. It applies the same validator chain, so this exercises
    /// exactly the checks a host would hit.
    /// </remarks>
    private static Action Resolving(MemoryOptions options) => () =>
    {
        var services = CoreServices();
        services.AddAgentMemoryCore(options);
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;
    };

    private static MemoryOptions WithRecall(Func<RecallOptions, RecallOptions> configure) =>
        new() { Recall = configure(RecallOptions.Default) };

    private static MemoryOptions WithDerived(Action<DerivedMemoryOptions> configure)
    {
        var options = new MemoryOptions();
        configure(options.Extraction.DerivedMemory);
        return options;
    }

    public static TheoryData<string, MemoryOptions> InvalidCoreOptions() => new()
    {
        { "MaxDueItems negative", WithRecall(r => r with { MaxDueItems = -1 }) },
        { "ExpiringWindow zero", WithRecall(r => r with { ExpiringWindow = TimeSpan.Zero }) },
        { "DueLookback negative", WithRecall(r => r with { DueLookback = TimeSpan.FromDays(-1) }) },
        { "TombstoneProbeTopK zero", WithRecall(r => r with { TombstoneProbeTopK = 0 }) },
        { "AccessTrackingQueueCapacity zero", new MemoryOptions { AccessTrackingQueueCapacity = 0 } },
        { "MaxDerivedFactsPerBatch zero", WithDerived(d => d.MaxDerivedFactsPerBatch = 0) },
        { "MaxGroupFanIn one", WithDerived(d => d.MaxGroupFanIn = 1) },
        { "MaxEnumerationItems zero", WithDerived(d => d.MaxEnumerationItems = 0) },
        { "DerivedFactConfidence above one", WithDerived(d => d.DerivedFactConfidence = 1.5) },
        { "DerivedFactConfidence negative", WithDerived(d => d.DerivedFactConfidence = -0.1) },
    };

    [Theory]
    [MemberData(nameof(InvalidCoreOptions))]
    public void AnInvalidCoreOptionFailsAtStartupRatherThanAtTheFirstAffectedCall(
        string name, MemoryOptions options)
    {
        Resolving(options).Should().Throw<OptionsValidationException>(
            "{0} must fail closed; it otherwise misconfigures silently and reads as a broken feature",
            name);
    }

    [Fact]
    public void TheShippedDefaultsAllValidate()
    {
        // The other direction, and it matters as much: a validator whose bound excludes the default
        // makes the library unusable out of the box.
        Resolving(new MemoryOptions()).Should().NotThrow();
    }

    [Fact]
    public void MaxGroupFanInOfOneIsRejectedBecauseNoOperatorCanAggregateOneFact()
    {
        // Not an arbitrary bound. Every evaluator refuses a group of fewer than two, so a cap of 1
        // disables arithmetic memory entirely while the flag still reads as enabled -- a configuration
        // that produces exactly the "measured no effect" result the feature would then be blamed for.
        Resolving(WithDerived(d => d.MaxGroupFanIn = 1))
            .Should().Throw<OptionsValidationException>();

        Resolving(WithDerived(d => d.MaxGroupFanIn = 2)).Should().NotThrow();
    }

    // ── the Agent Framework side ──────────────────────────────────────

    private static Action ResolvingAgentOptions(Action<AgentFrameworkOptions> configure) => () =>
    {
        var services = new ServiceCollection().AddLogging();
        services.AddOptions<AgentFrameworkOptions>().Configure(configure);
        services.AddOptions<AgentFrameworkOptions>()
            .Validate(o => o.MaxDeltaItemsPerSection > 0, "cap")
            .Validate(o => o.MinimumDeltaGap > TimeSpan.Zero, "gap")
            .Validate(o => !string.IsNullOrWhiteSpace(o.DefaultDeltaCheckpointKey), "key");
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<AgentFrameworkOptions>>().Value;
    };

    [Fact]
    public void AZeroDeltaCapIsRejected()
    {
        // Every bucket would report as fully truncated while returning nothing.
        ResolvingAgentOptions(o => o.MaxDeltaItemsPerSection = 0)
            .Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void ANonPositiveDeltaGapIsRejected()
    {
        // A gap of zero treats EVERY turn as a session resume, so the delta block appears on each one
        // and the feature that exists to catch a user up becomes a per-turn tax.
        ResolvingAgentOptions(o => o.MinimumDeltaGap = TimeSpan.Zero)
            .Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void ABlankCheckpointKeyIsRejected()
    {
        ResolvingAgentOptions(o => o.DefaultDeltaCheckpointKey = "  ")
            .Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void TheShippedAgentFrameworkDefaultsValidate()
    {
        ResolvingAgentOptions(_ => { }).Should().NotThrow();
    }
}
