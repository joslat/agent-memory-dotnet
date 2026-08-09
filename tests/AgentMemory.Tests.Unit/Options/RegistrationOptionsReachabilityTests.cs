using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core;
using Xunit;

// Namespace is OptionsTests, not Options: a sibling namespace named Options shadows
// Microsoft.Extensions.Options for every other file under AgentMemory.Tests.Unit.
namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// K9. Can the public registration API configure the memory system at all?
/// </summary>
/// <remarks>
/// <see cref="ServiceCollectionExtensions.AddAgentMemoryCore"/> takes an
/// <c>Action&lt;MemoryOptions&gt;</c> and hands it to <c>.Configure(...)</c>, which mutates one
/// instance in place. But <see cref="MemoryOptions"/> is a record whose properties are all
/// <c>init</c>-only, so the lambda body cannot assign them — <c>options.EnableGraphRag = true</c> is
/// a compile error (CS8852). The only shape that compiles is <c>options = options with { ... }</c>,
/// which rebinds the parameter local and discards the result the moment the lambda returns.
/// <para>
/// That is the shape the BlendedAgent sample ships, in both its <c>Program.cs</c> and its README.
/// It compiles, runs, and configures nothing. These tests exist to state which of the two readings
/// is true, because "the sample is wrong" and "the API is unusable" are not the same finding and
/// reading the code cannot separate them.
/// </para>
/// <para>
/// <c>Isolation</c> is included as the control: it is a mutable class rather than an init record —
/// the shape adopted for issue #100 — so if the mechanism itself worked, that knob would move while
/// the record-backed ones did not.
/// </para>
/// </remarks>
public sealed class RegistrationOptionsReachabilityTests
{
    [Fact]
    public void TheSamplesConfigureLambdaLeavesGraphRagOff()
    {
        // Verbatim the shape in samples/AgentMemory.Sample.BlendedAgent/Program.cs.
        var options = Resolve(o =>
        {
            o = o with
            {
                EnableGraphRag = true,
                Recall = new RecallOptions { MaxGraphRagItems = 5, MaxFacts = 10 }
            };
        });

        options.EnableGraphRag.Should().BeFalse(
            "the lambda rebinds its own parameter; the registered instance is never touched");
    }

    [Fact]
    public void NoRecordBackedRecallKnobIsReachable()
    {
        var options = Resolve(o => o = o with { Recall = new RecallOptions { MaxFacts = 999 } });

        options.Recall.MaxFacts.Should().Be(RecallOptions.Default.MaxFacts);
    }

    [Fact]
    public void TheMutableClassOptionIsReachable()
    {
        // The control. Isolation is a class with a settable property, so it configures normally.
        // This is what separates "records are unconfigurable" from "the whole mechanism is broken".
        var options = Resolve(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant);

        options.Isolation.Mode.Should().Be(MemoryIsolationMode.StrictMultiTenant);
    }

    private static MemoryOptions Resolve(Action<MemoryOptions> configure) =>
        new ServiceCollection()
            .AddAgentMemoryCore(configure)
            .BuildServiceProvider()
            .GetRequiredService<IOptions<MemoryOptions>>()
            .Value;
}
