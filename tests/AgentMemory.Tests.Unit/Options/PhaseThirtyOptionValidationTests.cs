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

    private static MemoryOptions WithWorkingMemory(Action<WorkingMemoryOptions> configure)
    {
        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        configure(options.WorkingMemory);
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

        // Wave C's working-memory tier — the THIRD time this phase that a feature shipped options
        // nothing validated. These are worse than most: three of them become a Cypher LIMIT, and a
        // rebuild failure is deliberately swallowed (the write must still succeed), so a negative cap
        // produces a warning in a log nobody is reading and a block that never exists.
        { "WorkingMemory MaxTokens zero", WithWorkingMemory(w => w.MaxTokens = 0) },
        { "WorkingMemory MaxStableFacts negative", WithWorkingMemory(w => w.MaxStableFacts = -1) },
        { "WorkingMemory MaxActivePreferences negative", WithWorkingMemory(w => w.MaxActivePreferences = -1) },
        { "WorkingMemory MaxTopEntities negative", WithWorkingMemory(w => w.MaxTopEntities = -1) },
        { "WorkingMemory MinFactMentionCount zero", WithWorkingMemory(w => w.MinFactMentionCount = 0) },
        { "WorkingMemory MinPreferenceConfidence above one", WithWorkingMemory(w => w.MinPreferenceConfidence = 1.5) },
        { "WorkingMemory MinPreferenceConfidence negative", WithWorkingMemory(w => w.MinPreferenceConfidence = -0.1) },

        // 30.10 fan-out -- the FOURTH feature this phase to ship numeric options with no validator,
        // and the first found by an EXTERNAL audit rather than by us, which is the part worth noticing.
        { "FanOut MaxSubQueries zero", WithFanOut(f => f.MaxSubQueries = 0) },
        { "FanOut MaxSubQueries negative", WithFanOut(f => f.MaxSubQueries = -1) },
        { "FanOut MinDistinctEntityMentions zero", WithFanOut(f => f.MinDistinctEntityMentions = 0) },
        { "FanOut WeakTopScoreThreshold above one", WithFanOut(f => f.WeakTopScoreThreshold = 1.5) },
        { "FanOut WeakTopScoreThreshold negative", WithFanOut(f => f.WeakTopScoreThreshold = -0.1) },
    };

    private static MemoryOptions WithFanOut(Action<RecallFanOutOptions> configure)
    {
        var options = new MemoryOptions();
        options.FanOut.Enabled = true;
        configure(options.FanOut);
        return options;
    }

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

    /// <summary>
    /// Zero is allowed for the three section caps, because omitting a section is a real intent —
    /// but not for the token budget, which would disable the whole tier while it reads as on.
    /// </summary>
    [Fact]
    public void AZeroSectionCapIsAllowed_ButAZeroTokenBudgetIsNot()
    {
        Resolving(WithWorkingMemory(w => w.MaxStableFacts = 0)).Should().NotThrow(
            "omitting the facts section is a configuration, not a mistake");
        Resolving(WithWorkingMemory(w => w.MaxTopEntities = 0)).Should().NotThrow();

        Resolving(WithWorkingMemory(w => w.MaxTokens = 0)).Should().Throw<OptionsValidationException>(
            "a zero budget renders nothing at all, which is indistinguishable from the tier being broken");
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
