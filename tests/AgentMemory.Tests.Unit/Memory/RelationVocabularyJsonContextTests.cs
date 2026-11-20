using System.Text.Json;
using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// The relation vocabulary reads the same through generated metadata (a host with reflection-based serialization off,
/// such as the Native AOT speech library) as through the reflection call every other host uses.
/// </summary>
public sealed class RelationVocabularyJsonContextTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static Stream Resource() =>
        typeof(RelationVocabularyDocument).Assembly.GetManifestResourceStream("AgentMemory.Core.Memory.relation-vocabulary.json")
        ?? throw new InvalidOperationException("relation vocabulary resource missing");

    [Fact]
    public void The_embedded_vocabulary_reads_the_same_through_generated_metadata()
    {
        RelationVocabularyDocument? reflection, generated;
        using (var stream = Resource())
            reflection = JsonSerializer.Deserialize<RelationVocabularyDocument>(stream, CaseInsensitive);
        using (var stream = Resource())
            generated = JsonSerializer.Deserialize(stream, RelationVocabularyJsonContext.Default.RelationVocabularyDocument);

        generated.Should().NotBeNull();
        generated!.Canonical.Should().NotBeEmpty();
        JsonSerializer.Serialize(generated).Should().Be(JsonSerializer.Serialize(reflection));
    }

    [Fact]
    public void Generated_metadata_matches_property_names_without_regard_to_case_like_the_reflection_call()
    {
        const string json = """{"SCHEMAVERSION":3,"Retired":["old"],"canonical":{"works_at":{"FAMILY":"state","sources":["s"]}}}""";
        var reflection = JsonSerializer.Deserialize<RelationVocabularyDocument>(json, CaseInsensitive);
        var generated = JsonSerializer.Deserialize(json, RelationVocabularyJsonContext.Default.RelationVocabularyDocument);

        generated!.SchemaVersion.Should().Be(3);
        generated.Canonical["works_at"].Family.Should().Be("state");
        JsonSerializer.Serialize(generated).Should().Be(JsonSerializer.Serialize(reflection));
    }
}
