using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleStoreProjectionTests
{
    private const string Profile = """
        {"format":"agentmemory.modules/v1alpha1",
         "stores":{"a":{"provider":"neo4j-direct"},"b":{"provider":"neo4j-direct"},"empty":{"provider":"neo4j-direct"}},
         "modules":[{"id":"com.example.first","version":"[1,2)","store":"a","manifest":"first.json"},
                    {"id":"com.example.second","version":"[1,2)","store":"b","manifest":"second.json"},
                    {"id":"com.example.third","version":"[1,2)","store":"b","manifest":"third.json"}]}
        """;
    private static ModuleManifest Manifest(string name, string? dependency = null) => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example." + name,
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Modules = dependency is null ? new Dictionary<string, ModuleVersionRange>()
            : new Dictionary<string, ModuleVersionRange> { ["com.example." + dependency] = ModuleVersionRange.Parse("[1,2)") }
    };
    private static ModuleProfileResolution Resolve(string json = Profile, bool missingDependency = false) =>
        ModuleProfileResolver.Resolve(ModuleCompositionProfile.Parse(json),
            [Manifest("third", "second"), Manifest("second", missingDependency ? "missing" : "first"), Manifest("first")],
            ModuleVersion.Parse("0.1.0"), new HashSet<string>(),
            new Dictionary<string,string> { ["a"] = "neo4j-direct", ["b"] = "neo4j-direct", ["empty"] = "neo4j-direct" });

    [Fact]
    public void KeepsDependencyOrderWithoutRequiringCrossStoreDependenciesInTheSlice()
    {
        var result = Resolve();
        result.Succeeded.Should().BeTrue();
        var slice = result.ForStore("b");
        slice.Succeeded.Should().BeTrue();
        slice.Modules.Select(m => m.Id).Should().Equal("com.example.second", "com.example.third");
        slice.Modules[0].Modules.Should().ContainKey("com.example.first");
        ((ICollection<ModuleManifest>)slice.Modules).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void DeclaredEmptyStoreIsDistinctFromAnUnknownStore()
    {
        var result = Resolve();
        result.ForStore("empty").Modules.Should().BeEmpty();
        result.ForStore("empty").ManifestFingerprint.Should().HaveLength(64);
        FluentActions.Invoking(() => result.ForStore("missing")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => result.ForStore("B")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AFailedWholeProfileCannotProduceAValidLookingStoreSlice()
    {
        var result = Resolve(missingDependency: true);
        result.Succeeded.Should().BeFalse();
        FluentActions.Invoking(() => result.ForStore("a")).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FingerprintBindsWholeProfileAndSelectedStore()
    {
        var result = Resolve();
        result.ForStore("a").ManifestFingerprint.Should().NotBe(result.ForStore("b").ManifestFingerprint);
        result.ForStore("b").ManifestFingerprint.Should().Be(Resolve().ForStore("b").ManifestFingerprint);
        result.ForStore("b").ManifestFingerprint.Should().NotBe(Resolve(Profile + " ").ForStore("b").ManifestFingerprint);
        result.ForStore("b").ManifestFingerprint.Should().NotBe(result.CompositionFingerprint);
    }
}
