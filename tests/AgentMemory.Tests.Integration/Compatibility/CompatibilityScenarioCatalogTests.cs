using System.Text.RegularExpressions;
using FluentAssertions;

namespace AgentMemory.Tests.Integration.Compatibility;

[Trait("Category", "Compatibility")]
public sealed class CompatibilityScenarioCatalogTests
{
    private static readonly Regex ScnIdPattern = new(@"^SCN-[BSGP]-\d{3}$", RegexOptions.Compiled);

    [Fact]
    public void ScenarioIds_AreUnique()
    {
        CompatibilityScenarioCatalog.Scenarios
            .Select(s => s.Id)
            .Should()
            .OnlyHaveUniqueItems();
    }

    [Fact]
    public void Catalog_ExposesRequestedBehavioralPack()
    {
        CompatibilityScenarioCatalog.Scenarios.Should().Contain(s => s.Id == "NET-TCK-B-001");
        CompatibilityScenarioCatalog.Scenarios.Should().Contain(s => s.Feature.Contains("Owner isolation"));
        CompatibilityScenarioCatalog.Scenarios.Should().Contain(s => s.Feature.Contains("real-provider", StringComparison.OrdinalIgnoreCase));
        CompatibilityScenarioCatalog.Scenarios.Should().Contain(s => s.Feature.Contains("read audit", StringComparison.OrdinalIgnoreCase));
        CompatibilityScenarioCatalog.Scenarios.Should().Contain(s => s.Feature.Contains("Recency/frequency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Catalog_CoversUpstreamTierLanguage()
    {
        CompatibilityScenarioCatalog.Scenarios.Select(s => s.Tier)
            .Should()
            .Contain(["Bronze", "Silver", "Gold"]);
    }

    [Fact]
    public void UpstreamScenarioIds_MatchScnPattern()
    {
        foreach (var scenario in CompatibilityScenarioCatalog.Scenarios)
        {
            foreach (var scnId in scenario.UpstreamScenarioIds)
            {
                ScnIdPattern.IsMatch(scnId).Should().BeTrue(
                    $"'{scnId}' on row '{scenario.Id}' should match the ^SCN-[BSGP]-\\d{{3}}$ pattern");
            }
        }
    }

    [Fact]
    public void UpstreamScenarioIds_AreUniqueAcrossCatalog()
    {
        var allScnIds = CompatibilityScenarioCatalog.Scenarios
            .SelectMany(s => s.UpstreamScenarioIds)
            .ToList();

        allScnIds.Should().OnlyHaveUniqueItems("no upstream SCN-* scenario should be claimed by more than one catalog row");
    }

    [Fact]
    public void BronzeUpstreamMirror_HasMapping()
    {
        // Scoped to the Bronze-tier, upstream-mirrored row only: Silver/Gold rows are intentionally
        // populated with an empty UpstreamScenarioIds placeholder this slice (see catalog comments),
        // so this must NOT be a blanket "all rows have mappings" assertion.
        CompatibilityScenarioCatalog.Scenarios
            .Single(s => s.Id == "NET-TCK-B-001")
            .UpstreamScenarioIds
            .Should()
            .NotBeEmpty();
    }
}
