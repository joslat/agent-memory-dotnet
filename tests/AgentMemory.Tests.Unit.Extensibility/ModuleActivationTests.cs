using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// 40.34-static: one registration composes modules for the process lifetime. Options bind from the module's section
/// and are validated; every identity a module adds is in its own namespace; the set is fingerprinted and frozen.
/// </summary>
public sealed class ModuleActivationTests
{
    private const string Reviews = "com.example.reviews";
    private const string Topics = "com.example.topics";

    private static ModuleManifest Manifest(string id, string version = "1.0.0", string extra = "") => ModuleManifest.Parse(
        $$"""{"format":"agentmemory.module/v1alpha1","id":"{{id}}","version":"{{version}}","sdk":"[0.1,0.2)"{{extra}}}""");

    private const string Ontology =
        ""","ontology":{"labelPrefix":"Review","types":[{"name":"ReviewItem","properties":{"title":{"type":"string"}}}]}""";

    private static IConfiguration Configuration(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static ServiceCollection Services(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IMemoryContextAssembler>());
        var isolation = Substitute.For<IMemoryIsolationPolicy>();
        isolation.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.For("owner"));
        services.AddSingleton(isolation);
        if (configuration is not null) services.AddSingleton(configuration);
        return services;
    }

    private static ServiceProvider Build(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    public sealed class ReviewOptions
    {
        public int Limit { get; set; } = 3;
        public string? Tone { get; set; }
    }

    private sealed class PositiveLimit : IValidateOptions<ReviewOptions>
    {
        public ValidateOptionsResult Validate(string? name, ReviewOptions options) =>
            options.Limit > 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Limit must be positive.");
    }

    private sealed class Contributor(string id, params string[] sections) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } = new(id, sections.Length == 0 ? new HashSet<string> { id } : [.. sections]);
        public string? Text { get; init; }
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ContextSection?>(new(Descriptor.SectionTypes.First(), Descriptor.Id, 1, [new("one", Text ?? "item")]));
    }

    private sealed class Provider(string id) : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor { get; } = new() { Id = id };
        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModuleProposal>>([]);
    }

    [Fact]
    public async Task AModuleIsComposedOnceAndItsContributorReachesTheCompiler()
    {
        var created = 0;
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => b.AddContextContributor(c =>
        {
            created++;
            c.Module.Manifest.Id.Should().Be(Reviews);
            return new Contributor(Reviews + ":open") { Text = "Review the Lyon plan" };
        })));
        using var provider = Build(services);

        var set = provider.GetRequiredService<ModuleSet>();
        provider.GetRequiredService<ModuleSet>().Should().BeSameAs(set, "one module set for the process lifetime");
        set.Modules.Select(x => x.Manifest.Id).Should().Equal(Reviews);
        set.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        using var scope = provider.CreateScope();
        var envelope = await scope.ServiceProvider.GetRequiredService<IContextCompiler>()
            .CompileAsync(new ContextRequest { SessionId = "s", Owner = "owner", Query = "what is next?" });
        envelope.Sections.Should().ContainSingle().Which.Items.Single().Text.Should().Be("Review the Lyon plan");
        envelope.Sections[0].Binding!.ModuleId.Should().Be(Reviews);
        scope.ServiceProvider.GetServices<IContextContributor>().Should().ContainSingle();
        created.Should().Be(1, "contributors are created once per scope");
    }

    [Fact]
    public void OptionsBindFromTheModulesOwnSectionAndAreFrozen()
    {
        var configuration = Configuration(("AgentMemory:Modules:" + Reviews + ":Limit", "7"), ("AgentMemory:Modules:" + Reviews + ":Tone", "brief"));
        ReviewOptions? seen = null;
        var services = Services(configuration);
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b =>
        {
            b.Configure(new PositiveLimit());
            b.AddContextContributor(c => { seen = c.GetOptions<ReviewOptions>(); return new Contributor(Reviews + ":open"); });
        }));
        using var provider = Build(services);
        var set = provider.GetRequiredService<ModuleSet>();
        configuration["AgentMemory:Modules:" + Reviews + ":Limit"] = "9";
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<IContextContributor>().Should().ContainSingle();

        seen!.Limit.Should().Be(7, "the set binds once; no reload");
        seen.Tone.Should().Be("brief");
        set.Modules[0].ConfigurationSection.Should().Be("AgentMemory:Modules:" + Reviews);
        set.Modules[0].ConfigurationSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        set.Modules[0].GetOptions<ReviewOptions>().Should().BeSameAs(seen);
    }

    [Fact]
    public void InvalidOptionsFailActivationWithEveryProblemAndAHostValidatorAlsoRuns()
    {
        var services = Services(Configuration(("AgentMemory:Modules:" + Reviews + ":Limit", "0")));
        services.AddSingleton<IValidateOptions<ReviewOptions>>(new ValidateOptions<ReviewOptions>(null, o => o.Tone is not null, "Tone is required."));
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => b.Configure(new PositiveLimit())));
        using var provider = Build(services);

        var act = () => provider.GetRequiredService<ModuleSet>();
        var error = act.Should().Throw<ModuleActivationException>().Which;
        error.Errors.Should().HaveCount(2);
        error.Errors.Should().Contain(e => e.Contains("Limit must be positive.", StringComparison.Ordinal))
            .And.Contain(e => e.Contains("Tone is required.", StringComparison.Ordinal));
        using var scope = provider.CreateScope();
        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<IContextCompiler>())
            .Should().Throw<ModuleActivationException>("everything built on the set fails closed");
    }

    [Fact]
    public void AProfileSuppliesTheConfigurationSectionAndMustNameEveryModule()
    {
        var profile = ModuleCompositionProfile.Parse($$$"""
            {"format":"agentmemory.modules/v1alpha1","stores":{"memory":{"provider":"none"}},
             "modules":[{"id":"{{{Reviews}}}","version":"[1,2)","store":"memory","manifest":"reviews.json","configuration":"Reviews"}]}
            """);
        var services = Services(Configuration(("Reviews:Limit", "5")));
        services.AddAgentMemoryModules(m => m.UseProfile(profile).Add(Manifest(Reviews), b => b.Configure<ReviewOptions>()));
        using (var provider = Build(services))
        {
            var set = provider.GetRequiredService<ModuleSet>();
            set.Modules[0].ConfigurationSection.Should().Be("Reviews");
            set.Modules[0].GetOptions<ReviewOptions>().Limit.Should().Be(5);
        }

        var extra = Services();
        extra.AddAgentMemoryModules(m => m.UseProfile(profile).Add(Manifest(Reviews), _ => { }).Add(Manifest(Topics), _ => { }));
        using var refused = Build(extra);
        FluentActions.Invoking(() => refused.GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
            .Which.Errors.Should().Contain(e => e.Contains("registered but not composed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("com.example.topics:open", null, "contributor")]
    [InlineData("com.example.reviews:open", "com.example.topics:items", "section")]
    [InlineData("open", null, "contributor")]
    public void AModuleCannotAddAContributorOrSectionOutsideItsNamespace(string id, string? section, string what)
    {
        var services = Services();
        services.AddAgentMemoryModules(m => m
            .Add(Manifest(Topics), _ => { })
            .Add(Manifest(Reviews), b => b.AddContextContributor(_ => section is null ? new Contributor(id) : new Contributor(id, section))));
        using var provider = Build(services);
        using var scope = provider.CreateScope();
        FluentActions.Invoking(() => scope.ServiceProvider.GetServices<IContextContributor>().ToList())
            .Should().Throw<InvalidOperationException>().WithMessage($"*'{Reviews}' added {what}*outside its own namespace*");
    }

    [Fact]
    public void AModuleCannotAddAnIngestionProviderOutsideItsNamespace()
    {
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Topics), _ => { })
            .Add(Manifest(Reviews), b => b.AddIngestionProvider(_ => new Provider(Topics + ":writer"))));
        using var provider = Build(services);
        using var scope = provider.CreateScope();
        FluentActions.Invoking(() => scope.ServiceProvider.GetRequiredService<IModuleIngestion>())
            .Should().Throw<InvalidOperationException>().WithMessage("*added ingestion provider 'com.example.topics:writer'*");
    }

    [Fact]
    public void TheFingerprintFollowsConfigurationAndVersionAndNothingElse()
    {
        string Fingerprint(string limit, string version, string tone = "brief")
        {
            var services = Services(Configuration(("AgentMemory:Modules:" + Reviews + ":Limit", limit), ("AgentMemory:Modules:" + Reviews + ":Tone", tone)));
            services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews, version), b => b.Configure<ReviewOptions>()));
            using var provider = Build(services);
            return provider.GetRequiredService<ModuleSet>().Fingerprint;
        }

        var baseline = Fingerprint("3", "1.0.0");
        Fingerprint("3", "1.0.0").Should().Be(baseline);
        Fingerprint("4", "1.0.0").Should().NotBe(baseline);
        Fingerprint("3", "1.0.0", "long").Should().NotBe(baseline);
        Fingerprint("3", "1.0.1").Should().NotBe(baseline);
    }

    [Fact]
    public async Task AModuleWithAnOntologyNeedsAStoreAndNeo4jSuppliesOne()
    {
        var bare = Services();
        bare.AddAgentMemoryModules(m => m.Add(Manifest(Reviews, extra: Ontology), _ => { }));
        using (var provider = Build(bare))
            FluentActions.Invoking(() => provider.GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
                .Which.Errors.Should().Contain(e => e.Contains("ModuleSchema", StringComparison.Ordinal));

        var neo4j = Services();
        neo4j.AddNeo4jAgentMemory(o => o.Uri = "bolt://127.0.0.1:1");
        neo4j.AddAgentMemoryModules(m => m.UseNeo4jStore().Add(Manifest(Reviews, extra: Ontology),
            b => b.AddIngestionProvider(_ => new Provider(Reviews + ":writer"))));
        // The store's runner owns a driver, which only disposes asynchronously; no connection is opened.
        await using var active = Build(neo4j);
        active.GetRequiredService<ModuleSet>().Modules.Should().ContainSingle();
        await using var scope = active.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IModuleIngestion>().Should().NotBeNull("the provider's module is bound to the Neo4j store");
        scope.ServiceProvider.GetRequiredService<IModuleStore>().Should().NotBeNull();
    }

    [Fact]
    public void ModulesAreAddedOnceAndComposedOnce()
    {
        FluentActions.Invoking(() => Services().AddAgentMemoryModules(m => m.Add(Manifest(Reviews), _ => { }).Add(Manifest(Reviews), _ => { })))
            .Should().Throw<ArgumentException>().WithMessage("*already added*");
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), _ => { }));
        FluentActions.Invoking(() => services.AddAgentMemoryModules(m => m.Add(Manifest(Topics), _ => { })))
            .Should().Throw<InvalidOperationException>().WithMessage("*one module set*");
        FluentActions.Invoking(() => Services().AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b =>
        {
            b.Configure<ReviewOptions>();
            b.Configure<ReviewOptions>();
        }))).Should().Throw<InvalidOperationException>().WithMessage("*already configures*");
    }

    [Fact]
    public void AValueTheBinderCannotConvertIsAListedProblem()
    {
        var services = Services(Configuration(("AgentMemory:Modules:" + Reviews + ":Limit", "many")));
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => b.Configure<ReviewOptions>()));
        using var provider = Build(services);
        FluentActions.Invoking(() => provider.GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
            .Which.Errors.Should().ContainSingle().Which.Should().Contain("does not bind");
    }

    [Fact]
    public void AModuleCannotDeclareAfterItWasAdded()
    {
        IModuleBuilder? leaked = null;
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => leaked = b));
        FluentActions.Invoking(() => leaked!.AddContextContributor(_ => new Contributor(Reviews + ":late")))
            .Should().Throw<InvalidOperationException>().WithMessage("*only while it is being added*");
    }

    [Fact]
    public void OptionsWithoutAnyConfigurationFailActivation()
    {
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => b.Configure<ReviewOptions>()));
        using var provider = Build(services);
        FluentActions.Invoking(() => provider.GetRequiredService<ModuleSet>()).Should().Throw<ModuleActivationException>()
            .Which.Errors.Should().ContainSingle().Which.Should().Contain("no IConfiguration");
    }

    [Fact]
    public void AModuleThatReadsWithoutAStoreIsToldWhy()
    {
        var services = Services();
        services.AddAgentMemoryModules(m => m.Add(Manifest(Reviews), b => b.AddContextContributor(c =>
        {
            _ = c.Reader;
            return new Contributor(Reviews + ":open");
        })));
        using var provider = Build(services);
        using var scope = provider.CreateScope();
        FluentActions.Invoking(() => scope.ServiceProvider.GetServices<IContextContributor>().ToList())
            .Should().Throw<InvalidOperationException>().WithMessage("*no module store is bound*");
    }
}
