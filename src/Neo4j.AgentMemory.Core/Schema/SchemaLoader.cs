using System.Text.Json;
using System.Text.Json.Serialization;
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Core.Schema;

/// <summary>
/// Loads and constructs <see cref="EntitySchemaConfig"/> from JSON files or streams,
/// and provides built-in POLE+O and legacy schema factories.
/// YAML is not supported to avoid third-party dependencies; use JSON.
/// </summary>
public static class SchemaLoader
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Loads an <see cref="EntitySchemaConfig"/> from a JSON file at the given path.</summary>
    /// <exception cref="FileNotFoundException">When the file does not exist.</exception>
    /// <exception cref="JsonException">When the JSON is malformed or cannot be deserialized.</exception>
    public static EntitySchemaConfig LoadFromJson(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Schema file not found: {path}", path);

        using var stream = File.OpenRead(path);
        return LoadFromJson(stream);
    }

    /// <summary>Loads an <see cref="EntitySchemaConfig"/> from a JSON stream.</summary>
    /// <exception cref="JsonException">When the JSON is malformed or cannot be deserialized.</exception>
    public static EntitySchemaConfig LoadFromJson(Stream stream)
    {
        var dto = JsonSerializer.Deserialize<EntitySchemaConfigDto>(stream, _jsonOptions)
                  ?? throw new JsonException("Deserialized schema config was null.");

        return MapFromDto(dto);
    }

    /// <summary>
    /// Creates a minimal custom schema containing only the specified entity types.
    /// The first type in the list becomes the default entity type.
    /// </summary>
    public static EntitySchemaConfig CreateForTypes(
        IReadOnlyList<string> entityTypes,
        bool enableSubtypes = false)
    {
        var types = entityTypes
            .Select(t => new EntityTypeConfig { Name = t.ToUpperInvariant() })
            .ToList();

        return new EntitySchemaConfig
        {
            Name              = "custom",
            Version           = "1.0",
            Description       = "Custom entity schema",
            EntityTypes       = types,
            RelationTypes     = Array.Empty<RelationTypeConfig>(),
            DefaultEntityType = entityTypes.Count > 0 ? entityTypes[0].ToUpperInvariant() : "OBJECT",
            EnableSubtypes    = enableSubtypes,
        };
    }

    /// <summary>Returns the default POLE+O schema.</summary>
    public static EntitySchemaConfig GetDefaultSchema() => new();

    /// <summary>Returns the legacy schema (PERSON, ORGANIZATION, LOCATION, EVENT, CONCEPT, EMOTION, PREFERENCE, FACT).</summary>
    public static EntitySchemaConfig GetLegacySchema() => new()
    {
        Name              = "legacy",
        Version           = "1.0",
        Description       = "Legacy entity schema for backward compatibility",
        EntityTypes       = DefaultSchemas.GetLegacyEntityTypes(),
        RelationTypes     = Array.Empty<RelationTypeConfig>(),
        DefaultEntityType = "CONCEPT",
        EnableSubtypes    = false,
    };

    // ── private helpers ──────────────────────────────────────────────────────────

    private static EntitySchemaConfig MapFromDto(EntitySchemaConfigDto dto)
    {
        var entityTypes = dto.EntityTypes?
            .Select(e => new EntityTypeConfig
            {
                Name        = e.Name ?? throw new JsonException("EntityTypeConfig.Name is required."),
                Description = e.Description,
                Subtypes    = (IReadOnlyList<string>?)e.Subtypes ?? Array.Empty<string>(),
                Attributes  = (IReadOnlyList<string>?)e.Attributes ?? Array.Empty<string>(),
                Color       = e.Color,
            })
            .ToList()
            ?? (IReadOnlyList<EntityTypeConfig>) DefaultSchemas.GetPoleoEntityTypes();

        var relationTypes = dto.RelationTypes?
            .Select(r => new RelationTypeConfig
            {
                Name        = r.Name ?? throw new JsonException("RelationTypeConfig.Name is required."),
                Description = r.Description,
                SourceTypes = (IReadOnlyList<string>?)r.SourceTypes ?? Array.Empty<string>(),
                TargetTypes = (IReadOnlyList<string>?)r.TargetTypes ?? Array.Empty<string>(),
                Properties  = (IReadOnlyList<string>?)r.Properties ?? Array.Empty<string>(),
            })
            .ToList()
            ?? (IReadOnlyList<RelationTypeConfig>) DefaultSchemas.GetPoleoRelationTypes();

        return new EntitySchemaConfig
        {
            Name              = dto.Name              ?? "poleo",
            Version           = dto.Version           ?? "1.0",
            Description       = dto.Description,
            EntityTypes       = entityTypes,
            RelationTypes     = relationTypes,
            DefaultEntityType = dto.DefaultEntityType ?? "OBJECT",
            EnableSubtypes    = dto.EnableSubtypes    ?? true,
            StrictTypes       = dto.StrictTypes       ?? false,
        };
    }

    // ── DTO types for JSON deserialization ────────────────────────────────────────

    private sealed class EntitySchemaConfigDto
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public List<EntityTypeConfigDto>? EntityTypes { get; set; }
        public List<RelationTypeConfigDto>? RelationTypes { get; set; }
        public string? DefaultEntityType { get; set; }
        public bool? EnableSubtypes { get; set; }
        public bool? StrictTypes { get; set; }
    }

    private sealed class EntityTypeConfigDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? Subtypes { get; set; }
        public List<string>? Attributes { get; set; }
        public string? Color { get; set; }
    }

    private sealed class RelationTypeConfigDto
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? SourceTypes { get; set; }
        public List<string>? TargetTypes { get; set; }
        public List<string>? Properties { get; set; }
    }
}
