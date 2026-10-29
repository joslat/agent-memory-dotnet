using System.Security.Cryptography;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-require (review 2.17, 4.6): a host can make trials mandatory in chosen namespaces, so a pack there that declares no
/// trial stays off instead of skipping the gate; outside them the format's choice stands.
/// </summary>
public sealed class PackRequireTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pack-require-").FullName;
    private readonly ECDsa _host = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private static ModuleManifest Manifest(string id, string extra = "") => ModuleManifest.Parse(
        $$"""{"format":"agentmemory.module/v1alpha1","id":"{{id}}","version":"1.0.0","sdk":"[0.1,0.2)"{{extra}}}""");

    [Fact]
    public void In_a_mandatory_namespace_a_pack_without_a_trial_stays_off_and_so_does_what_needs_it()
    {
        var untrialled = Manifest("io.example.dojo");
        var needsIt = Manifest("com.other.coach", ""","requires":{"modules":{"io.example.dojo":"[1,2)"}}""");
        var elsewhere = Manifest("io.examples.notes");

        var set = Compose(m => m.Add(untrialled, _ => { }).Add(needsIt, _ => { }).Add(elsewhere, _ => { })
            .RequireTrials(Activations()).RequireTrialsFor("io.example"));

        set.Modules.Select(m => m.Manifest.Id).Should().Equal("io.examples.notes");
        set.SwitchedOff.Should().BeEquivalentTo([
            new SwitchedOffModule("com.other.coach", "1.0.0", "it needs io.example.dojo, which is off"),
            new SwitchedOffModule("io.example.dojo", "1.0.0", "it declares no trial, and this host requires one for io.example"),
        ]);
    }

    [Fact]
    public void Without_a_mandatory_namespace_a_pack_without_a_trial_composes_as_before()
    {
        var set = Compose(m => m.Add(Manifest("io.example.dojo"), _ => { }).RequireTrials(Activations()));

        set.Modules.Should().ContainSingle();
        set.SwitchedOff.Should().BeEmpty();
    }

    [Fact]
    public void A_mandatory_namespace_needs_the_hosts_decisions()
    {
        FluentActions.Invoking(() => Compose(m => m.Add(Manifest("io.example.dojo"), _ => { }).RequireTrialsFor("io.example")))
            .Should().Throw<ModuleActivationException>().Which.Message.Should().Contain("call RequireTrials too");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".io")]
    [InlineData("io..example")]
    [InlineData("io example")]
    [InlineData("IO.Example")]
    [InlineData("io.example..")]
    [InlineData("io.-example")]
    public void A_namespace_is_a_pack_id_prefix(string prefix) =>
        FluentActions.Invoking(() => new ServiceCollection().AddAgentMemoryModules(m => m.RequireTrialsFor(prefix)))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("io.example", "io.example", true)]
    [InlineData("io.example", "io.example.dojo", true)]
    [InlineData("io.example.", "io.example.dojo", true)]
    [InlineData("io.example", "io.examples", false)]
    [InlineData("io.example", "io.exampl", false)]
    [InlineData("io.example.dojo", "io.example", false)]
    public void A_namespace_covers_an_id_by_whole_segments_exactly_as_a_trust_policy_does(string prefix, string id, bool covered)
    {
        ModuleSetBuilder.Covers(prefix, id).Should().Be(covered);
        PackManifest.Covers(prefix, id).Should().Be(covered, "the trust policy and the trial requirement scope ids by one rule");
    }

    [Theory]
    [InlineData("io", true)]
    [InlineData("io.example", true)]
    [InlineData("io.example.", true)]
    [InlineData("io.ex-ample.dojo", true)]
    [InlineData("", false)]
    [InlineData(".io", false)]
    [InlineData("io..example", false)]
    [InlineData("io.example..", false)]
    [InlineData("IO.Example", false)]
    [InlineData("io.-example", false)]
    [InlineData("io example", false)]
    [InlineData("io.1example", false)]
    public void A_prefix_is_valid_by_one_rule_for_the_trust_policy_and_the_hosts_namespaces(string prefix, bool valid)
    {
        // Review 1, L17: the two rules are twins; this keeps them the same.
        ModuleSetBuilder.IsValidPrefix(prefix).Should().Be(valid);
        PackManifest.IsValidPrefix(prefix).Should().Be(valid);
    }

    private PackActivationFolder Activations() => new(_folder, _host.ExportSubjectPublicKeyInfoPem());

    private static ModuleSet Compose(Action<ModuleSetBuilder> add)
    {
        var services = new ServiceCollection();
        services.AddAgentMemoryModules(add);
        return services.BuildServiceProvider().GetRequiredService<ModuleSet>();
    }
}
