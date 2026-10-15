using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Nams;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleHostProfileTests
{
    private static ModuleManifest Manifest(params string[] features) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example.reviews",
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"), Host = features
    };
    private static ModuleManifest SchemaManifest(params string[] features) => Manifest(features) with
    { Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewItem" }] } };
    private static readonly ModuleVersion Sdk = ModuleVersion.Parse("0.1.0");
    private static ModuleProfileResolution Resolve(ModuleHostStore store, ModuleManifest manifest) =>
        ModuleHostProfileResolver.Resolve(ModuleCompositionProfile.Parse("""
            {"format":"agentmemory.modules/v1alpha1","stores":{"memory":{"provider":"neo4j-direct"}},
             "modules":[{"id":"com.example.reviews","version":"[1,2)","store":"memory","manifest":"never-open.json"}]}
            """), [manifest], Sdk, new Dictionary<string, ModuleHostStore> { ["memory"] = store });
    private static ModuleHostStore Store(IServiceProvider services, string? key = null) => new()
    {
        Provider = "neo4j-direct", Services = services, ServiceKey = key,
        SchemaProbe = new Neo4jModuleSchemaCapabilityProbe()
    };
    private static ServiceCollection Neo4jServices()
    {
        var services = new ServiceCollection();
        services.AddNeo4jAgentMemory(o => o.Uri = "bolt://127.0.0.1:1");
        // Actual store type, lacking a ModuleResolution until host validation succeeds.
        services.AddScoped<IModuleStore, Neo4jModuleStore>();
        return services;
    }

    [Fact]
    public void ActualNeo4jRegistrationsQualifyWithoutConstructingStoreOrOpeningDatabase()
    {
        using var services = Neo4jServices().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Resolve(Store(services), SchemaManifest("History")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EmptyOrNamsShapedHostCannotAcquireSchemaCapabilityFromManifest()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = Resolve(Store(services), SchemaManifest("History"));
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ModuleSchema", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("History", StringComparison.Ordinal));
        result.Modules.Should().BeEmpty(); result.CompositionFingerprint.Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SchemaNeedsBothStoreAndRunnerWithoutInvokingFactories(bool store, bool runner)
    {
        var collection = new ServiceCollection();
        if (store) collection.AddScoped<IModuleStore>(_ => throw new InvalidOperationException("Must not construct store"));
        if (runner) collection.AddSingleton<INeo4jTransactionRunner>(_ => throw new InvalidOperationException("Must not construct runner"));
        using var services = collection.BuildServiceProvider();
        Resolve(Store(services), SchemaManifest()).Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, ValidTimeMode.Ignore, false)]
    [InlineData(true, ValidTimeMode.Ignore, false)]
    [InlineData(false, ValidTimeMode.Current, false)]
    [InlineData(true, ValidTimeMode.Current, true)]
    public void RemindersRequireBothEffectiveOptions(bool firing, ValidTimeMode time, bool expected)
    {
        using var services = new ServiceCollection().AddSingleton<IOptions<MemoryOptions>>(
            Options.Create(new MemoryOptions { Recall = new RecallOptions { ProspectiveFiring = firing, ValidTime = time } }))
            .BuildServiceProvider();
        Resolve(Store(services), Manifest("Reminders")).Succeeded.Should().Be(expected);
    }

    [Fact]
    public void KeyedStoreCannotBorrowUnkeyedCapabilities()
    {
        using var services = Neo4jServices().BuildServiceProvider();
        var result = Resolve(Store(services, "isolated"), SchemaManifest("History"));
        result.Succeeded.Should().BeFalse(); result.Modules.Should().BeEmpty();
    }

    [Fact]
    public void OntologyDerivesSchemaRequirementWithoutExplicitHostFlag()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var result = Resolve(Store(services), Manifest() with
        { Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewItem" }] } });
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("ModuleSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void ActualNamsCompositionCannotSupplyNeo4jSchemaEvenIfHostClaimsThatProvider()
    {
        var collection = new ServiceCollection();
        collection.AddNamsAgentMemory(o => o.Endpoint = new Uri("https://127.0.0.1:1"));
        using var services = collection.BuildServiceProvider();
        var result = Resolve(Store(services), SchemaManifest("History"));
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("INeo4jTransactionRunner", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("IModuleStore", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("IMemoryHistoryService", StringComparison.Ordinal));
        result.Modules.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachStoreMustSupplyItsOwnCapabilities(bool secondReady)
    {
        using var first = Neo4jServices().BuildServiceProvider();
        using var second = (secondReady ? Neo4jServices() : new ServiceCollection()).BuildServiceProvider();
        var profile = ModuleCompositionProfile.Parse("""
            {"format":"agentmemory.modules/v1alpha1",
             "stores":{"a":{"provider":"neo4j-direct"},"b":{"provider":"neo4j-direct"}},
             "modules":[{"id":"com.example.reviews","version":"[1,2)","store":"a","manifest":"a.json"},
                        {"id":"com.example.other","version":"[1,2)","store":"b","manifest":"b.json"}]}
            """);
        var other = SchemaManifest() with { Id = "com.example.other",
            Ontology = new ModuleOntology { LabelPrefix = "Other", Types = [new ModuleType { Name = "OtherItem" }] } };
        var result = ModuleHostProfileResolver.Resolve(profile, [SchemaManifest(), other], Sdk,
            new Dictionary<string, ModuleHostStore> { ["a"] = Store(first), ["b"] = Store(second) });
        result.Succeeded.Should().Be(secondReady);
        if (secondReady) result.ForStore("b").Modules.Should().ContainSingle(m => m.Id == "com.example.other");
        else
        {
            result.Errors.Should().OnlyContain(e => e.StartsWith("Store 'b'", StringComparison.Ordinal));
            result.Modules.Should().BeEmpty(); result.CompositionFingerprint.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyedSchemaRequiresRunnerAtTheSameKey(bool matchingRunner)
    {
        var collection = new ServiceCollection();
        collection.AddKeyedScoped<IModuleStore>("module-store", (_, _) => throw new InvalidOperationException("Store construction forbidden"));
        collection.AddKeyedSingleton<INeo4jTransactionRunner>(matchingRunner ? "module-store" : "elsewhere",
            (_, _) => throw new InvalidOperationException("Runner construction forbidden"));
        using var services = collection.BuildServiceProvider();
        Resolve(Store(services, "module-store"), SchemaManifest()).Succeeded.Should().Be(matchingRunner);
    }

    [Fact]
    public void KeyedReminderOptionsDoNotInheritEnabledRootOptions()
    {
        var enabled = Options.Create(new MemoryOptions
        { Recall = new RecallOptions { ProspectiveFiring = true, ValidTime = ValidTimeMode.Current } });
        var collection = new ServiceCollection().AddSingleton<IOptions<MemoryOptions>>(enabled);
        collection.AddKeyedSingleton<IOptions<MemoryOptions>>("isolated", Options.Create(new MemoryOptions()));
        using var services = collection.BuildServiceProvider();
        Resolve(Store(services), Manifest("Reminders")).Succeeded.Should().BeTrue();
        Resolve(Store(services, "isolated"), Manifest("Reminders")).Succeeded.Should().BeFalse();
        Resolve(Store(services, "missing"), Manifest("Reminders")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void MissingOptionsAndInvalidOptionsRefuseWithoutLeakingValidationText()
    {
        using var missing = new ServiceCollection().BuildServiceProvider();
        Resolve(Store(missing), Manifest("Reminders")).Succeeded.Should().BeFalse();
        var collection = new ServiceCollection();
        collection.AddOptions<MemoryOptions>().Validate(_ => false, "sensitive host configuration detail");
        using var invalid = collection.BuildServiceProvider();
        var result = Resolve(Store(invalid), Manifest("Reminders"));
        result.Succeeded.Should().BeFalse();
        string.Join(" ", result.Errors).Should().NotContain("sensitive");
    }

    [Fact]
    public void MissingOrWrongAdapterProbeDoesNotGrantSchema()
    {
        using var services = Neo4jServices().BuildServiceProvider();
        Resolve(Store(services) with { SchemaProbe = null }, SchemaManifest()).Succeeded.Should().BeFalse();
        var result = Resolve(Store(services) with { SchemaProbe = new WrongProviderProbe() }, SchemaManifest());
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidMetadataStopsBeforeAnyHostServiceOrAdapterProbe()
    {
        var store = Store(new ForbiddenServiceProvider()) with { SchemaProbe = new WrongProviderProbe() };
        var result = Resolve(store, SchemaManifest() with { Sdk = ModuleVersionRange.Parse("[9,10)") });
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("SDK", StringComparison.Ordinal));
    }

    [Fact]
    public void NoRequiredCapabilitiesMeansNoHostServiceCalls()
    {
        Resolve(Store(new ForbiddenServiceProvider()), Manifest()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void ContainerWithoutRegistrationInspectionRefusesInsteadOfGuessing()
    {
        var result = Resolve(Store(new OpaqueServiceProvider()), SchemaManifest());
        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("cannot inspect", StringComparison.Ordinal));
    }

    private sealed class ForbiddenServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new InvalidOperationException("No services may be queried");
    }
    private sealed class OpaqueServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
    private sealed class WrongProviderProbe : IModuleSchemaCapabilityProbe
    {
        public string Provider => "different";
        public IReadOnlyList<string> GetProblems(IServiceProvider services, string? serviceKey = null) =>
            throw new InvalidOperationException("Mismatched probe must not be called");
    }
}
