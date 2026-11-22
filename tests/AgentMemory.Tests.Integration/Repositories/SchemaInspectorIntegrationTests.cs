using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// NATIVE-SCHEMA: <see cref="ISchemaInspector"/> reads what the store holds of the schema, the same comparison as the
/// CLI's schema-check, and changes nothing. The shared store is bootstrapped by its fixture at 4 dimensions.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SchemaInspectorIntegrationTests(Neo4jIntegrationFixture fixture)
{
    private SchemaInspector Inspector(int dimensions) => new(fixture.TransactionRunner,
        Options.Create(new Neo4jOptions { Uri = fixture.ConnectionString, Username = fixture.User, Password = fixture.Password, EmbeddingDimensions = dimensions }));

    [Fact]
    public async Task A_bootstrapped_store_is_complete_at_its_dimensions()
    {
        var inspection = await Inspector(Neo4jIntegrationFixture.TestEmbeddingDimensions).InspectAsync();

        inspection.Complete.Should().BeTrue(string.Join(", ", inspection.Missing));
        inspection.Empty.Should().BeFalse();
        inspection.Expected.Should().BeGreaterThan(10);
        inspection.VectorDimensions.Should().NotBeEmpty().And.OnlyContain(p => p.Value == Neo4jIntegrationFixture.TestEmbeddingDimensions);
        inspection.DimensionsMatch.Should().BeTrue();
    }

    [Fact]
    public async Task The_same_store_read_for_other_dimensions_says_they_do_not_match()
    {
        var inspection = await Inspector(1024).InspectAsync();

        inspection.DimensionsMatch.Should().BeFalse();
        inspection.ConfiguredDimensions.Should().Be(1024);
    }

    [Fact]
    public void Vector_dimensions_come_from_the_index_config_whatever_number_type_the_driver_gives()
    {
        object Options(object value) => new Dictionary<string, object> { ["indexConfig"] = new Dictionary<string, object> { ["vector.dimensions"] = value } };

        SchemaInspector.VectorDimensionsOf(Options(1024L)).Should().Be(1024);
        SchemaInspector.VectorDimensionsOf(Options(384)).Should().Be(384);
        SchemaInspector.VectorDimensionsOf(new Dictionary<string, object>()).Should().BeNull();
        SchemaInspector.VectorDimensionsOf(null).Should().BeNull();
    }
}
