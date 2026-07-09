using FluentAssertions;

namespace AgentMemory.Tests.Integration.Compatibility;

[Trait("Category", "Compatibility")]
public sealed class CompatibilityScenarioCatalogTests
{
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
}
