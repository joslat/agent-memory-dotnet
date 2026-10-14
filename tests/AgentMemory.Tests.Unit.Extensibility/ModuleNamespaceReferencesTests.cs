using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleNamespaceReferencesTests
{
    private static ModuleManifest Module(string prefix = "Review") => new()
    {
        Format = "agentmemory.module/v1alpha1", Id = "com.example.reviews",
        Version = ModuleVersion.Parse("1.0.0"), Sdk = ModuleVersionRange.Parse("[0.1,0.2)"),
        Ontology = new ModuleOntology { LabelPrefix = prefix }
    };

    [Fact]
    public void HistoricalOwnedNamesNeedNotAppearInTheCurrentOntology() =>
        ModuleNamespaceReferences.Validate(Module(), ["ReviewLegacy"], ["REVIEW_LEGACY_LINK"], ["old;quoted index"])
            .Should().BeEmpty();

    [Fact]
    public void AcronymPrefixUsesTheSameNormalizationAsManifestRelations() =>
        ModuleNamespaceReferences.Validate(Module("HTTPReview"), ["HTTPReviewItem"], ["HTTP_REVIEW_LINK"], [])
            .Should().BeEmpty();

    [Theory]
    [InlineData("Fact")]
    [InlineData("OtherItem")]
    [InlineData("reviewItem")]
    [InlineData("Review_Item")]
    [InlineData("")]
    [InlineData(null)]
    public void ForeignOrMalformedLabelIsRejected(string? name) =>
        ModuleNamespaceReferences.Validate(Module(), [name!], [], []).Should().NotBeEmpty();

    [Theory]
    [InlineData("OTHER_LINK")]
    [InlineData("REVIEW")]
    [InlineData("REVIEWING_LINK")]
    [InlineData("Review_LINK")]
    [InlineData("")]
    [InlineData(null)]
    public void RelationshipRequiresExactNormalizedPrefixAndValidSuffix(string? name) =>
        ModuleNamespaceReferences.Validate(Module(), [], [name!], []).Should().NotBeEmpty();

    [Theory]
    [InlineData("Fact", "Fact")]
    [InlineData("Module", "ModuleMigration")]
    public void PrefixDoesNotAuthorizeReservedLabels(string prefix, string label) =>
        ModuleNamespaceReferences.Validate(Module(prefix), [label], [], []).Should().Contain(e => e.Contains("reserved"));

    [Fact]
    public void PrefixDoesNotAuthorizeReservedRelationships() =>
        ModuleNamespaceReferences.Validate(Module("Has"), [], ["HAS_FACT"], []).Should().Contain(e => e.Contains("reserved"));

    [Theory]
    [InlineData("fact_id")]
    [InlineData("module_migration_key")]
    [InlineData("module_key_id")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("review\nindex")]
    [InlineData(null)]
    public void ReservedOrMalformedObjectIdentityIsRejected(string? name) =>
        ModuleNamespaceReferences.Validate(Module(), [], [], [name!]).Should().NotBeEmpty();

    [Fact]
    public void ReferencesRequireOntologyButEmptyCodeOnlyModuleDoesNot()
    {
        var module = Module() with { Ontology = null };
        ModuleNamespaceReferences.Validate(module, [], [], []).Should().BeEmpty();
        ModuleNamespaceReferences.Validate(module, ["ReviewItem"], [], []).Should().NotBeEmpty();
        ModuleNamespaceReferences.Validate(module, [], [], ["index"]).Should().NotBeEmpty();
    }

    [Fact]
    public void InvalidManifestIsNotLaunderedByAValidPrefix() =>
        ModuleNamespaceReferences.Validate(Module() with { Id = "invalid" }, ["ReviewItem"], [], [])
            .Should().Contain(e => e.StartsWith("$.id"));

    [Fact]
    public void ErrorsAreDeterministicDistinctAndReadOnly()
    {
        var errors = ModuleNamespaceReferences.Validate(Module(), ["Other", "Other"], ["BAD"], ["fact_id"]);
        errors.Should().BeInAscendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();
        var action = () => ((IList<string>)errors).Add("change");
        action.Should().Throw<NotSupportedException>();
    }
}
