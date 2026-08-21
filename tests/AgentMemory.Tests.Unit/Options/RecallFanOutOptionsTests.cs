using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Repositories;
using NSubstitute;

// OptionsTests, not Options: a namespace segment named Options shadows Microsoft.Extensions.Options.
namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// Defaults and configurability for <see cref="RecallFanOutOptions"/> (30.10).
/// </summary>
/// <remarks>
/// <para>
/// A test per default, which looks excessive and is not. This repository has twice shipped a feature
/// whose option diverged from its documented default, and the failure is silent in both directions:
/// a host reads the docs, sets nothing, and gets behaviour nobody described.
/// </para>
/// <para>
/// The configurability test is the #100 lesson: a sub-option reached through <c>MemoryOptions</c>
/// must be assignable from the <c>configureMemory</c> lambda hosts actually use. An init-only record
/// cannot be, and the option then silently cannot be configured at all.
/// </para>
/// </remarks>
public sealed class RecallFanOutOptionsTests
{
    [Fact]
    public void FanOutIsOffByDefault()
    {
        // The whole byte-identical-when-off guarantee rests on this one default.
        new RecallFanOutOptions().Enabled.Should().BeFalse();
        new MemoryOptions().FanOut.Enabled.Should().BeFalse();
    }

    [Fact]
    public void TheDeterministicDeriverIsTheDefault()
    {
        // Reproducibility: a model deriving the sub-queries makes every downstream number depend on a
        // sampled output, and a mechanism whose measurement cannot be repeated cannot be shown to work.
        new RecallFanOutOptions().UseLlmDerivation.Should().BeFalse();
    }

    [Fact]
    public void MaxSubQueriesDefaultsToFour()
    {
        new RecallFanOutOptions().MaxSubQueries.Should().Be(4);
    }

    [Fact]
    public void TheWeakScoreSignalIsDisabledByDefault()
    {
        // Null, not 0.7. That number is a measured dead zone on ONE corpus, and shipping it as a
        // default would bake one corpus's calibration into every host's recall.
        new RecallFanOutOptions().WeakTopScoreThreshold.Should().BeNull();
    }

    [Fact]
    public void MinDistinctEntityMentionsDefaultsToThree()
    {
        new RecallFanOutOptions().MinDistinctEntityMentions.Should().Be(3);
    }

    [Fact]
    public void EveryFanOutOptionIsSettableThroughConfigureMemory()
    {
        // The #100 regression guard. If FanOut ever becomes an init-only record this stops compiling,
        // which is the point: the failure it prevents is silent at runtime.
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>());

        services.AddAgentMemoryCore(options =>
        {
            options.FanOut.Enabled = true;
            options.FanOut.UseLlmDerivation = true;
            options.FanOut.MaxSubQueries = 2;
            options.FanOut.WeakTopScoreThreshold = 0.55;
            options.FanOut.MinDistinctEntityMentions = 5;
        });

        using var provider = services.BuildServiceProvider();
        var fanOut = provider.GetRequiredService<IOptions<MemoryOptions>>().Value.FanOut;

        fanOut.Enabled.Should().BeTrue();
        fanOut.UseLlmDerivation.Should().BeTrue();
        fanOut.MaxSubQueries.Should().Be(2);
        fanOut.WeakTopScoreThreshold.Should().Be(0.55);
        fanOut.MinDistinctEntityMentions.Should().Be(5);
    }
}
