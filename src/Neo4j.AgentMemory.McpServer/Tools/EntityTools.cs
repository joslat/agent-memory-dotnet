using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Entity tools: get entity, create relationship.
/// </summary>
[McpServerToolType]
public sealed class EntityTools
{
    [McpServerTool(Name = "memory_get_entity"), Description("Get entities by name or search for entities. Returns matching entities with their relationships.")]
    public static async Task<string> MemoryGetEntity(
        ILongTermMemoryService longTermMemory,
        [Description("Name to search for (searches exact and alias matches)")] string name,
        CancellationToken cancellationToken = default)
    {
        var entities = await longTermMemory.GetEntitiesByNameAsync(name, includeAliases: true, cancellationToken);
        return ToolJsonContext.Serialize(entities.Select(e => new
        {
            e.EntityId,
            e.Name,
            e.CanonicalName,
            e.Type,
            e.Subtype,
            e.Description,
            e.Confidence,
            e.Aliases,
            e.CreatedAtUtc
        }));
    }

    [McpServerTool(Name = "memory_create_relationship"), Description("Create a relationship between two entities in the knowledge graph.")]
    public static async Task<string> MemoryCreateRelationship(
        ILongTermMemoryService longTermMemory,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<McpServerOptions> options,
        [Description("Entity ID of the source entity")] string sourceEntityId,
        [Description("Entity ID of the target entity")] string targetEntityId,
        [Description("Type of relationship (e.g., 'WORKS_FOR', 'LOCATED_IN', 'KNOWS')")] string relationshipType,
        [Description("Description of the relationship (optional)")] string? description = null,
        [Description("Confidence score from 0.0 to 1.0 (optional)")] double? confidence = null,
        CancellationToken cancellationToken = default)
    {
        var relationship = new Relationship
        {
            RelationshipId = idGenerator.GenerateId(),
            SourceEntityId = sourceEntityId,
            TargetEntityId = targetEntityId,
            RelationshipType = relationshipType,
            Description = description,
            Confidence = confidence ?? options.Value.DefaultConfidence,
            CreatedAtUtc = clock.UtcNow
        };

        var result = await longTermMemory.AddRelationshipAsync(relationship, cancellationToken);
        return ToolJsonContext.Serialize(new
        {
            result.RelationshipId,
            result.SourceEntityId,
            result.TargetEntityId,
            result.RelationshipType,
            result.Description,
            result.Confidence,
            result.CreatedAtUtc
        });
    }
}
