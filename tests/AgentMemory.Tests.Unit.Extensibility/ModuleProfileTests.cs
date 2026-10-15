using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleProfileTests
{
    private const string Profile = """
        {"format":"agentmemory.modules/v1alpha1","stores":{"memory":{"provider":"neo4j-direct"}},
         "modules":[{"id":"com.example.reviews","version":"[1,2)","store":"memory",
         "assembly":"modules/Reviews.dll","configuration":"Features:Reviews"}]}
        """;
    private static ModuleManifest Manifest(string id = "com.example.reviews") => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = id, Version = ModuleVersion.Parse("1.0.0"),
        Sdk = ModuleVersionRange.Parse("[0.1,0.2)")
    };
    private static ModuleProfileResolution Resolve(string json, params ModuleManifest[] manifests) =>
        ModuleProfileResolver.Resolve(ModuleCompositionProfile.Parse(json), manifests,
            ModuleVersion.Parse("0.1.0"), new HashSet<string>(),
            new Dictionary<string, string> { ["memory"] = "neo4j-direct" });

    [Fact]
    public void ComposesMetadataAndConfigurationWithoutOpeningTheDeclaredAssembly()
    {
        var result = Resolve(Profile, Manifest());
        result.Succeeded.Should().BeTrue(); result.Errors.Should().BeEmpty();
        var module = result.Modules.Single();
        module.Manifest.Id.Should().Be("com.example.reviews");
        module.Binding.Assembly.Should().Be("modules/Reviews.dll");
        module.Binding.Manifest.Should().BeNull();
        module.ConfigurationSection.Should().Be("Features:Reviews");
        module.Provider.Should().Be("neo4j-direct");
        result.CompositionFingerprint.Should().HaveLength(64);
    }

    [Theory]
    [InlineData("\"manifest\":\"reviews.json\",\"assembly\":\"reviews.dll\"")]
    [InlineData("\"manifest\":null")]
    [InlineData("\"routing\":true")]
    public void InvalidOrAmbiguousSourcesFail(string replacement)
    {
        var json = Profile.Replace("\"assembly\":\"modules/Reviews.dll\"", replacement, StringComparison.Ordinal);
        FluentActions.Invoking(() => ModuleCompositionProfile.Parse(json)).Should().Throw<FormatException>();
    }

    [Fact]
    public void StrictWireChecksDuplicateEscapedFieldsUnknownFieldsAndInputLimit()
    {
        foreach (var json in new[] { Profile.Replace("\"stores\":", "\"stores\":{},\"st\\u006fres\":", StringComparison.Ordinal),
            Profile.Replace("\"provider\":", "\"typo\":true,\"provider\":", StringComparison.Ordinal),
            Profile + new string(' ', ModuleCompositionProfile.MaximumUtf8Bytes),
            Profile.Replace("[1,2)", "*", StringComparison.Ordinal) })
            FluentActions.Invoking(() => ModuleCompositionProfile.Parse(json)).Should().Throw<FormatException>();
    }

    [Fact]
    public void RefusesRegisteredButNotComposedAlongsideMissingProfileEntry()
    {
        var result = Resolve(Profile, Manifest("com.example.other"));
        result.Errors.Should().Contain(e => e.Contains("registered but not composed", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("not registered", StringComparison.Ordinal));
        result.Modules.Should().BeEmpty(); result.CompositionFingerprint.Should().BeNull();
    }

    [Fact]
    public void WrongProfileVersionAndStoreProviderBothFail()
    {
        var profile = ModuleCompositionProfile.Parse(Profile.Replace("[1,2)", "[2,3)", StringComparison.Ordinal));
        var result = ModuleProfileResolver.Resolve(profile, [Manifest()], ModuleVersion.Parse("0.1.0"),
            new HashSet<string>(), new Dictionary<string,string> { ["memory"] = "nams" });
        result.Errors.Should().Contain(e => e.Contains("version", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("provider", StringComparison.Ordinal));
        result.Succeeded.Should().BeFalse(); result.Modules.Should().BeEmpty();
    }

    [Fact]
    public void DataOnlySourceUsesDefaultSectionAndPreservesOptionalBundlePin()
    {
        var json = Profile.Replace("\"assembly\":\"modules/Reviews.dll\",\"configuration\":\"Features:Reviews\"",
            "\"manifest\":\"reviews.json\",\"sha256\":\"" + new string('a',64) + "\"", StringComparison.Ordinal);
        var result = Resolve(json, Manifest());
        result.Succeeded.Should().BeTrue();
        result.Modules[0].ConfigurationSection.Should().Be("AgentMemory:Modules:com.example.reviews");
        result.Modules[0].Binding.Sha256.Should().Be(new string('a',64));
    }

    [Fact]
    public void ProfileBytesChangeFingerprintButRegistrationsOrderDoesNot()
    {
        var a = ModuleCompositionProfile.Parse(Profile);
        var b = ModuleCompositionProfile.Parse(Profile + " ");
        a.Sha256.Should().NotBe(b.Sha256);
        Resolve(Profile, Manifest()).CompositionFingerprint.Should().NotBe(Resolve(Profile + " ", Manifest()).CompositionFingerprint);
        var profile = Profile.Replace("}]}", "},{\"id\":\"com.example.other\",\"version\":\"[1,2)\",\"store\":\"memory\",\"manifest\":\"other.json\"}]}", StringComparison.Ordinal);
        Resolve(profile, Manifest(), Manifest("com.example.other")).CompositionFingerprint.Should().Be(
            Resolve(profile, Manifest("com.example.other"), Manifest()).CompositionFingerprint);
        ((IDictionary<string, ModuleProfileStore>)a.Stores).IsReadOnly.Should().BeTrue();
        ((ICollection<ModuleProfileEntry>)a.Modules).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void EmptyProfileAndNoRegistrationsComposeWithoutStoreServices()
    {
        Resolve("{\"format\":\"agentmemory.modules/v1alpha1\",\"stores\":{},\"modules\":[]}").Succeeded.Should().BeTrue();
        Resolve(Profile.Replace("\"store\":\"memory\"", "\"store\":\"missing\"", StringComparison.Ordinal), Manifest())
            .Errors.Should().Contain(e => e.Contains("store", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphLinksCannotPromiseRelationshipsAcrossDifferentStores()
    {
        var profile = ModuleCompositionProfile.Parse("""
            {"format":"agentmemory.modules/v1alpha1","stores":{"a":{"provider":"neo4j-direct"},"b":{"provider":"neo4j-direct"}},
             "modules":[{"id":"com.example.reviews","version":"[1,2)","store":"a","manifest":"reviews.json"},
                        {"id":"com.example.topics","version":"[1,2)","store":"b","manifest":"topics.json"}]}
            """);
        var reviews = Manifest() with
        {
            Modules = new Dictionary<string,ModuleVersionRange> { ["com.example.topics"] = ModuleVersionRange.Parse("[1,2)") },
            Ontology = new ModuleOntology { LabelPrefix = "Review", Types = [new ModuleType { Name = "ReviewItem" }],
                Relations = [new ModuleRelation { Name = "REVIEW_TOPIC", From = "ReviewItem", To = "com.example.topics:TopicItem" }] }
        };
        var topics = Manifest("com.example.topics") with
        { Ontology = new ModuleOntology { LabelPrefix = "Topic", Types = [new ModuleType { Name = "TopicItem" }] } };
        var result = ModuleProfileResolver.Resolve(profile, [reviews, topics], ModuleVersion.Parse("0.1.0"),
            new HashSet<string> { "ModuleSchema" }, new Dictionary<string,string> { ["a"] = "neo4j-direct", ["b"] = "neo4j-direct" });
        result.Errors.Should().Contain(e => e.Contains("cross-store", StringComparison.Ordinal));
        result.Modules.Should().BeEmpty();
    }

}
