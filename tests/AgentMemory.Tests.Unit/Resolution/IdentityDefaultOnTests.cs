using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Extraction.Llm;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ILC-25: evidence-based identity is on by default wherever the store-aware writer runs on a backend with identity storage.
/// Everywhere else the previous resolver runs, and the memory tiers line says so, why, and what that resolver can get wrong.
/// The defaults are the configuration that was measured, so passing tests mean the shipped default behaves as measured.
/// </summary>
public sealed class IdentityDefaultOnTests
{
    private const string KnownIssue = "it can merge different things that share a name";

    /// <summary>
    /// The identity arm of the head-to-head, the busy variant and the ECB+ runs (tools/AgentMemory.Scenarios/Arms.cs,
    /// graph-identity-writer): 4 candidates, 2 second opinions of 1,024 tokens within 45 seconds, the conversational
    /// policies, 4 context turns, event details at recall, the writer's event identities and alias capture.
    /// </summary>
    [Fact]
    public void The_defaults_are_the_measured_configuration()
    {
        var identity = new IdentityResolutionOptions();
        identity.Enabled.Should().BeNull("automatic: on where the writer runs on identity storage");
        identity.MaxCandidates.Should().Be(4);
        identity.MaxAdjudicationsPerIngestion.Should().Be(2);
        identity.AdjudicatorMaxOutputTokens.Should().Be(1_024);
        identity.AdjudicationTimeout.Should().Be(TimeSpan.FromSeconds(45));
        identity.ContextTurns.Should().Be(4);
        identity.Policies.Should().BeEmpty("a host's own rules start empty");
        var automatic = IdentityConfiguration.Capture(identity, active: true, contextRootedLookup: true);
        foreach (var type in new[] { "EVENT", "PERSON", "ORGANIZATION" })
            automatic.PolicyFor(type).Should().Match<EntityIdentityPolicy>(p => p.AllowContextualConfirmation && p.ReviewContextRootedCandidates);
        automatic.PolicyFor("LOCATION").AllowContextualConfirmation.Should().BeFalse("every other type, places included, stays review-only");
        RecallOptions.Default.EventDetails.Should().BeNull("event details follow identity");
        RecallOptions.Default.MaxEventDetailFacts.Should().Be(8);
        new LlmExtractionOptions().MemoryWriterEventIdentities.Should().BeNull("the writer's event identities follow identity");
    }

    [Fact]
    public void Automatic_mode_leaves_a_host_s_rules_and_a_switched_on_host_s_policies_alone()
    {
        var hostRule = new IdentityResolutionOptions();
        hostRule.Policies["PERSON"] = new EntityIdentityPolicy { IdentityProperties = ["employeeId"] };
        var automatic = IdentityConfiguration.Capture(hostRule, active: true, contextRootedLookup: false);
        automatic.PolicyFor("PERSON").AllowContextualConfirmation.Should().BeFalse("a rule the host set wins");
        automatic.PolicyFor("EVENT").ReviewContextRootedCandidates.Should().BeFalse("only a backend that can look them up nominates context-turn candidates");
        automatic.PolicyFor("EVENT").AllowContextualConfirmation.Should().BeTrue();

        var switchedOn = IdentityConfiguration.Capture(new IdentityResolutionOptions { Enabled = true });
        switchedOn.PolicyFor("PERSON").AllowContextualConfirmation.Should().BeFalse("switched on, a host configures its own policies, as before");
    }

    [Fact]
    public void Where_the_writer_runs_on_identity_storage_identity_is_on_by_default()
    {
        using var provider = Build();
        provider.GetRequiredService<IdentityActivation>().Active.Should().BeTrue();
        provider.GetRequiredService<IMemoryTiers>().Line().Should().Contain("; identity: evidence-based; ");
    }

    [Fact]
    public void Without_the_writer_the_previous_resolver_runs_and_the_tiers_line_says_why()
    {
        using var provider = Build(writer: false);
        provider.GetRequiredService<IdentityActivation>().Active.Should().BeFalse();
        provider.GetRequiredService<IMemoryTiers>().Line().Should().Contain(
            $"identity: previous resolver (the store-aware writer is off; {KnownIssue})");
    }

    [Fact]
    public void Without_identity_storage_the_previous_resolver_runs_and_the_tiers_line_says_why()
    {
        using var provider = Build(storage: false);
        provider.GetRequiredService<IdentityActivation>().Active.Should().BeFalse();
        provider.GetRequiredService<IMemoryTiers>().Line().Should().Contain(
            $"identity: previous resolver (this backend has no identity storage; {KnownIssue})");
    }

    [Fact]
    public void Switched_off_it_is_the_previous_resolver_whatever_runs()
    {
        using var provider = Build(enabled: false);
        provider.GetRequiredService<IdentityActivation>().Active.Should().BeFalse();
        provider.GetRequiredService<IMemoryTiers>().Line().Should().Contain($"identity: previous resolver (switched off; {KnownIssue})");
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IdentityIngestion>().Enabled.Should().BeFalse();
    }

    [Fact]
    public void Switched_on_it_runs_without_the_writer()
    {
        using var provider = Build(enabled: true, writer: false);
        provider.GetRequiredService<IdentityActivation>().Active.Should().BeTrue();
    }

    [Fact]
    public void Context_turns_and_event_details_follow_identity()
    {
        using var on = Build();
        var active = on.GetRequiredService<IdentityActivation>();
        active.ContextTurns(0).Should().Be(4, "identity reads at least its own context turns");
        active.ContextTurns(6).Should().Be(6);
        active.EventDetails(null).Should().BeTrue();
        active.EventDetails(false).Should().BeFalse("an explicit choice wins");

        using var off = Build(enabled: false);
        var inactive = off.GetRequiredService<IdentityActivation>();
        inactive.ContextTurns(0).Should().Be(0, "off is the previous behaviour");
        inactive.EventDetails(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    public void The_writer_s_event_identities_and_alias_capture_follow_identity(bool? enabled, bool storeAwareWriter, bool expected)
    {
        using var provider = Build(enabled, writer: false, more: services =>
        {
            services.AddSingleton(Substitute.For<IChatClient>());
            services.AddLlmExtraction(o => { if (storeAwareWriter) o.ApplyRecommended(); else o.ApplyConversational(); });
        });
        var options = provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value;
        options.MemoryWriterEventIdentities.Should().Be(expected);
        options.CaptureIdentityAliases.Should().Be(expected);
        provider.GetRequiredService<IdentityActivation>().Active.Should().Be(expected);
    }

    [Fact]
    public async Task The_second_opinion_uses_the_extraction_model_by_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddLlmExtraction();
        await using var provider = services.BuildServiceProvider();
        provider.GetService<IIdentityAdjudicator>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dedicated_second_opinion_model_replaces_the_default_in_either_order(bool dedicatedFirst)
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IChatClient>());
        if (dedicatedFirst) services.AddIdentityAdjudication(_ => { calls++; return Substitute.For<IChatClient>(); });
        services.AddLlmExtraction();
        if (!dedicatedFirst) services.AddIdentityAdjudication(_ => { calls++; return Substitute.For<IChatClient>(); });
        services.Count(d => d.ServiceType == typeof(IIdentityAdjudicator)).Should().Be(1);
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdjudicator>();
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(false, true, false)]
    [InlineData(null, false, false)]
    public void The_neo4j_identity_schema_follows_identity(bool? enabled, bool writer, bool expected)
    {
        using var provider = Build(enabled, writer, storage: false, more: services =>
            AgentMemory.Neo4j.Infrastructure.ServiceCollectionExtensions.AddNeo4jAgentMemory(services, o =>
            {
                o.Uri = "bolt://unused.test:7687"; o.Username = "unused"; o.Password = "unused";
            }));
        provider.GetRequiredService<IOptions<Neo4jOptions>>().Value.Extensions.Contains("identity-evidence").Should().Be(expected);
    }

    private static ServiceProvider Build(bool? enabled = null, bool writer = true, bool storage = true, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(_ => { });
        if (enabled is not null) services.Configure<IdentityResolutionOptions>(o => o.Enabled = enabled);
        if (writer)
        {
            var memoryWriter = Substitute.For<IMemoryWriter>();
            memoryWriter.IsEnabled.Returns(true);
            services.AddSingleton(memoryWriter);
        }
        if (storage)
        {
            services.AddSingleton(Substitute.For<IIdentityDecisionRepository>());
            services.AddSingleton(Substitute.For<IIdentityCandidateProvider>());
        }
        more?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
