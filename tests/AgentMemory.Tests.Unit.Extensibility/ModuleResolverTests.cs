using System.Text.Json;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleResolverTests
{
    private static ModuleManifest Module(string name, params string[] dependencies) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + name, Version = ModuleVersion.Parse("1.0.0"),
        Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Modules = dependencies.ToDictionary(d => "com.example." + d, _ => ModuleVersionRange.Parse("[1.0,2.0)"))
    };
    private static ModuleResolution Resolve(params ModuleManifest[] modules) =>
        ModuleResolver.Resolve(modules, ModuleVersion.Parse("0.1.0"), new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public void DiamondUsesSharedDeterministicDependencyOrderAndInputOrderIndependentFingerprint()
    {
        var modules = new[] { Module("a", "b", "c"), Module("b", "d"), Module("c", "d"), Module("d") };
        var resolved = Resolve(modules);
        resolved.Succeeded.Should().BeTrue(); resolved.Errors.Should().BeEmpty();
        resolved.Modules.Select(m => m.Id).Should().Equal("com.example.d", "com.example.b", "com.example.c", "com.example.a");
        resolved.ManifestFingerprint.Should().HaveLength(64);
        Resolve(modules.Reverse().ToArray()).ManifestFingerprint.Should().Be(resolved.ManifestFingerprint);
    }

    [Fact]
    public void AggregatesMissingWrongVersionSdkAndFeatureErrorsWithoutExposingAPartialSet()
    {
        var a = Module("a", "missing", "b") with { Host = ["History"], Sdk = ModuleVersionRange.Parse("[9,10)") };
        var result = Resolve(a, Module("b") with { Version = ModuleVersion.Parse("3.0.0") });
        result.Succeeded.Should().BeFalse(); result.Modules.Should().BeEmpty(); result.ManifestFingerprint.Should().BeNull();
        result.Errors.Should().Contain(e => e.Contains("missing", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("3.0.0", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("SDK", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("History", StringComparison.Ordinal));
        result.Errors.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void MissingDependencyDoesNotHideAnIndependentCycle()
    {
        var result = Resolve(Module("a", "b", "missing"), Module("b", "a"));
        result.Errors.Should().Contain(e => e.Contains("missing", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIdentityIsRefusedEvenWhenItsManifestIsIdentical()
    {
        var a = Module("a");var result = Resolve(a,a);
        result.Errors.Should().Contain(e => e.Contains("duplicate module", StringComparison.Ordinal));
        result.Modules.Should().BeEmpty();
    }

    [Fact]
    public void InvalidDeclarationCannotHideADuplicateIdentity()
    {
        var a = Module("a");
        Resolve(a, a with { Host = ["Typo"] }).Errors.Should().Contain(e => e.Contains("duplicate module", StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotCannotBeChangedByMutatingTheOriginalCollectionsOrDisposingItsJson()
    {
        var modules = new Dictionary<string, ModuleVersionRange> { ["com.example.b"] = ModuleVersionRange.Parse("[1,2)") };
        var values = new[] { "new", "known" };
        var properties = new Dictionary<string, ModuleProperty> { ["state"] = new() { Type = "string", Values = values } };
        var types = new[] { new ModuleType { Name = "ReviewItem", Properties = properties } };
        using var doc = JsonDocument.Parse("\"new\"");
        var filters = new[] { new ModuleViewFilter { Property = "state", Operator = "=", Value = doc.RootElement } };
        var views = new[] { new ModuleView { Id = "new", Type = "ReviewItem", Limit = 3, Where = filters } };
        var a = Module("a") with { Modules = modules, Ontology = new ModuleOntology { LabelPrefix = "Review", Types = types }, Views = views };
        var result = ModuleResolver.Resolve([a,Module("b")], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        result.Succeeded.Should().BeTrue();var fingerprint = result.ManifestFingerprint;
        modules.Clear();values[0] = "changed";properties.Clear();types[0] = new ModuleType { Name = "Other" };filters[0] = filters[0] with { Operator = "!=" };views[0] = views[0] with { Limit = 999 };doc.Dispose();
        var frozen = result.Modules.Single(m => m.Id == a.Id);
        frozen.Modules.Should().ContainKey("com.example.b");
        frozen.Ontology!.Types[0].Properties["state"].Values.Should().Equal("new","known");
        frozen.Views[0].Where[0].Value.GetString().Should().Be("new");frozen.Views[0].Limit.Should().Be(3);
        ((ICollection<ModuleManifest>)result.Modules).IsReadOnly.Should().BeTrue();
        ((IDictionary<string, ModuleProperty>)frozen.Ontology.Types[0].Properties).IsReadOnly.Should().BeTrue();
        result.ManifestFingerprint.Should().Be(fingerprint);
    }

    [Fact]
    public void CallerConstructedInvalidManifestsFailLocalValidationBeforeComposition()
    {
        var result = Resolve(Module("a") with { Host = ["Typo"], Modules = null! });
        result.Succeeded.Should().BeFalse();result.Errors.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void SchemaRequirementIsDerivedEvenIfTheAuthorDidNotDeclareIt()
    {
        var a = Module("a") with { Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewItem" }] } };
        Resolve(a).Errors.Should().Contain(e => e.Contains("ModuleSchema", StringComparison.Ordinal));
        ModuleResolver.Resolve([a], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" }).Succeeded.Should().BeTrue();
        ModuleResolver.Resolve([a], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchmea" }).Succeeded.Should().BeFalse();
    }

    [Fact]
    public void CrossModuleLinksRequireAnActualDeclaredTargetType()
    {
        var a = Module("a", "b") with { Ontology = new ModuleOntology { LabelPrefix = "Review",
            Types = [new ModuleType { Name = "ReviewItem" }], Relations = [new ModuleRelation { Name = "REVIEW_TOPIC", From = "ReviewItem", To = "com.example.b:TopicItem" }] } };
        var b = Module("b") with { Ontology = new ModuleOntology { LabelPrefix = "Topic", Types = [new ModuleType { Name = "TopicOther" }] } };
        var result = ModuleResolver.Resolve([a,b], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        result.Errors.Should().Contain(e => e.Contains("TopicItem", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefixAndNormalizedLabelCollisionsAreRejectedAcrossModules()
    {
        var a = Module("a") with { Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewURL" }] } };
        var b = Module("b") with { Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewUrl" }] } };
        var result = ModuleResolver.Resolve([a,b], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
        result.Errors.Should().Contain(e => e.Contains("prefix", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("schema name", StringComparison.Ordinal));
    }

    [Fact]
    public void MetadataChangesAffectTheFingerprintAndEmptySetsAreValid()
    {
        Resolve().Succeeded.Should().BeTrue();Resolve().Modules.Should().BeEmpty();
        Resolve(Module("a") with { Version = ModuleVersion.Parse("1.0.1") }).ManifestFingerprint.Should().NotBe(Resolve(Module("a")).ManifestFingerprint);
    }
}
