using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Options;

namespace Neo4j.AgentMemory.Tests.Unit.OptionsTests;

public class RecallOptionsTests
{
    [Fact]
    public void Default_MaxRecentMessagesIs10()
    {
        var options = new RecallOptions();
        options.MaxRecentMessages.Should().Be(10);
    }

    [Fact]
    public void Default_MaxRelevantMessagesIs5()
    {
        var options = new RecallOptions();
        options.MaxRelevantMessages.Should().Be(5);
    }

    [Fact]
    public void Default_MaxEntitiesIs10()
    {
        var options = new RecallOptions();
        options.MaxEntities.Should().Be(10);
    }

    [Fact]
    public void Default_MaxPreferencesIs5()
    {
        var options = new RecallOptions();
        options.MaxPreferences.Should().Be(5);
    }

    [Fact]
    public void Default_MaxFactsIs10()
    {
        var options = new RecallOptions();
        options.MaxFacts.Should().Be(10);
    }

    [Fact]
    public void Default_MaxTracesIs3()
    {
        var options = new RecallOptions();
        options.MaxTraces.Should().Be(3);
    }

    [Fact]
    public void Default_MaxGraphRagItemsIs5()
    {
        var options = new RecallOptions();
        options.MaxGraphRagItems.Should().Be(5);
    }

    [Fact]
    public void Default_MinSimilarityScoreIs070()
    {
        var options = new RecallOptions();
        options.MinSimilarityScore.Should().Be(0.7);
    }

    [Fact]
    public void Default_BlendModeIsBlended()
    {
        var options = new RecallOptions();
        options.BlendMode.Should().Be(RetrievalBlendMode.Blended);
    }

    [Fact]
    public void Default_StaticDefaultMatchesNewInstance()
    {
        var instance = new RecallOptions();
        var staticDefault = RecallOptions.Default;

        staticDefault.MaxRecentMessages.Should().Be(instance.MaxRecentMessages);
        staticDefault.MaxEntities.Should().Be(instance.MaxEntities);
        staticDefault.BlendMode.Should().Be(instance.BlendMode);
    }

    [Fact]
    public void Default_AllMaxValuesArePositive()
    {
        var options = new RecallOptions();
        options.MaxRecentMessages.Should().BePositive();
        options.MaxRelevantMessages.Should().BePositive();
        options.MaxEntities.Should().BePositive();
        options.MaxFacts.Should().BePositive();
        options.MaxPreferences.Should().BePositive();
        options.MaxTraces.Should().BePositive();
        options.MaxGraphRagItems.Should().BePositive();
    }
}
