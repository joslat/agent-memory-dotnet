using System.Text;
using System.Text.Json;
using FluentAssertions;
using Neo4j.AgentMemory.Core.Schema;

namespace Neo4j.AgentMemory.Tests.Unit.Schema;

public sealed class SchemaLoaderTests : IDisposable
{
    private readonly string _tempFile;

    public SchemaLoaderTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"schema-test-{Guid.NewGuid()}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    // ── LoadFromJson (path) ──────────────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_ValidFile_ReturnsConfig()
    {
        WriteJson(_tempFile, BuildMinimalSchemaJson("medical", "2.0"));

        var config = SchemaLoader.LoadFromJson(_tempFile);

        config.Name.Should().Be("medical");
        config.Version.Should().Be("2.0");
        config.EntityTypes.Should().HaveCount(2);
    }

    [Fact]
    public void LoadFromJson_FileNotFound_ThrowsFileNotFoundException()
    {
        var action = () => SchemaLoader.LoadFromJson("C:\\no-such-file.json");
        action.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void LoadFromJson_InvalidJson_ThrowsException()
    {
        File.WriteAllText(_tempFile, "{ not valid json !!!");
        var action = () => SchemaLoader.LoadFromJson(_tempFile);
        action.Should().Throw<Exception>();
    }

    // ── LoadFromJson (stream) ────────────────────────────────────────────────────

    [Fact]
    public void LoadFromJson_Stream_ReturnsConfig()
    {
        var json = BuildMinimalSchemaJson("stream-schema", "1.5");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var config = SchemaLoader.LoadFromJson(stream);

        config.Name.Should().Be("stream-schema");
        config.Version.Should().Be("1.5");
    }

    [Fact]
    public void LoadFromJson_Stream_InvalidJson_ThrowsJsonException()
    {
        var bad = "{ broken"u8.ToArray();
        using var stream = new MemoryStream(bad);
        var action = () => SchemaLoader.LoadFromJson(stream);
        action.Should().Throw<JsonException>();
    }

    // ── CreateForTypes ───────────────────────────────────────────────────────────

    [Fact]
    public void CreateForTypes_CreatesCustomSchema()
    {
        var config = SchemaLoader.CreateForTypes(["PATIENT", "DRUG", "DIAGNOSIS"]);

        config.Name.Should().Be("custom");
        config.EntityTypes.Should().HaveCount(3);
        config.GetEntityTypeNames().Should().BeEquivalentTo(["PATIENT", "DRUG", "DIAGNOSIS"]);
    }

    [Fact]
    public void CreateForTypes_FirstTypeBecomesDefault()
    {
        var config = SchemaLoader.CreateForTypes(["PATIENT", "DRUG"]);
        config.DefaultEntityType.Should().Be("PATIENT");
    }

    [Fact]
    public void CreateForTypes_EmptyTypes_UsesObjectDefault()
    {
        var config = SchemaLoader.CreateForTypes([]);
        config.DefaultEntityType.Should().Be("OBJECT");
        config.EntityTypes.Should().BeEmpty();
    }

    [Fact]
    public void CreateForTypes_NormalizesToUppercase()
    {
        var config = SchemaLoader.CreateForTypes(["patient", "Drug"]);
        config.GetEntityTypeNames().Should().BeEquivalentTo(["PATIENT", "DRUG"]);
    }

    [Fact]
    public void CreateForTypes_EnableSubtypes_PassedThrough()
    {
        var withSubtypes    = SchemaLoader.CreateForTypes(["X"], enableSubtypes: true);
        var withoutSubtypes = SchemaLoader.CreateForTypes(["X"], enableSubtypes: false);

        withSubtypes.EnableSubtypes.Should().BeTrue();
        withoutSubtypes.EnableSubtypes.Should().BeFalse();
    }

    // ── GetDefaultSchema / GetLegacySchema ───────────────────────────────────────

    [Fact]
    public void GetDefaultSchema_ReturnsPoleO()
    {
        var schema = SchemaLoader.GetDefaultSchema();

        schema.Name.Should().Be("poleo");
        schema.EntityTypes.Should().HaveCount(5);
        schema.RelationTypes.Should().HaveCount(16);
    }

    [Fact]
    public void GetLegacySchema_ReturnsLegacyTypes()
    {
        var schema = SchemaLoader.GetLegacySchema();

        schema.Name.Should().Be("legacy");
        schema.EnableSubtypes.Should().BeFalse();
        schema.EntityTypes.Select(e => e.Name).Should().Contain("CONCEPT")
            .And.Contain("EMOTION")
            .And.Contain("PREFERENCE")
            .And.Contain("FACT");
    }

    [Fact]
    public void GetLegacySchema_DefaultEntityType_IsConcept()
    {
        SchemaLoader.GetLegacySchema().DefaultEntityType.Should().Be("CONCEPT");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static string BuildMinimalSchemaJson(string name, string version) =>
        $$"""
        {
          "name": "{{name}}",
          "version": "{{version}}",
          "description": "Test schema",
          "entityTypes": [
            { "name": "PATIENT", "description": "A patient" },
            { "name": "DRUG", "description": "A drug" }
          ],
          "relationTypes": [],
          "defaultEntityType": "PATIENT",
          "enableSubtypes": false,
          "strictTypes": false
        }
        """;

    private static void WriteJson(string path, string json) =>
        File.WriteAllText(path, json, Encoding.UTF8);
}
