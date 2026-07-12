using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Entity tools: get entity, provenance, create relationship.
/// </summary>
[McpServerToolType]
internal sealed class EntityTools
{
    [McpServerTool(Name = "memory_get_entity_provenance"), Description("Get the provenance of an entity for auditability: the source messages it was extracted from (with span/confidence) and the extractors that produced it (with confidence and timing).")]
    public static async Task<string> MemoryGetEntityProvenance(
        IExtractorRepository extractorRepository,
        [Description("Entity ID to fetch provenance for")] string entityId,
        [Description("Owner/user identifier (optional). When set, returns provenance only for that owner's own or shared entity (found=false otherwise); null = unscoped (admin/audit).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId);
        var provenance = await extractorRepository.GetProvenanceAsync(entityId, scope, cancellationToken).ConfigureAwait(false);
        if (provenance is null)
            return ToolJsonContext.Serialize(new { entityId, found = false });

        return ToolJsonContext.Serialize(new
        {
            provenance.EntityId,
            found = true,
            sources = provenance.Sources,
            extractors = provenance.Extractors
        });
    }

    [McpServerTool(Name = "memory_get_entity"), Description("Get entities by name (exact or alias match). Returns matching entities; use memory_create_relationship / graph queries for edges.")]
    public static async Task<string> MemoryGetEntity(
        ILongTermMemoryService longTermMemory,
        [Description("Name to search for (searches exact and alias matches)")] string name,
        [Description("Owner/user identifier (optional). Null = all owners (unscoped/admin); set it to return only that owner's plus shared (un-owned) entities. Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId);
        var entities = await longTermMemory.GetEntitiesByNameAsync(name, includeAliases: true, scope, cancellationToken).ConfigureAwait(false);
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
            e.CreatedAtUtc,
            e.UpdatedAtUtc
        }));
    }

    [McpServerTool(Name = "memory_record_entity_feedback"), Description("Record feedback on an entity by nudging its confidence: positive reinforces, negative penalizes (clamped to 0..1). Returns the updated entity, or found=false if it does not exist or is out of scope.")]
    public static async Task<string> MemoryRecordEntityFeedback(
        ILongTermMemoryService longTermMemory,
        [Description("Entity ID to apply feedback to")] string entityId,
        [Description("True to reinforce (increase confidence), false to penalize (decrease)")] bool positive,
        [Description("Magnitude of the confidence nudge (optional; defaults to the configured feedback delta)")] double? delta = null,
        [Description("Owner/user identifier (optional). When set, feedback only affects that user's own or shared entities — never another user's private entity.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrEmpty(userId) ? null : MemoryScope.For(userId);
        var entity = await longTermMemory.RecordEntityFeedbackAsync(entityId, positive, delta, scope, cancellationToken).ConfigureAwait(false);
        if (entity is null)
            return ToolJsonContext.Serialize(new { entityId, found = false });

        return ToolJsonContext.Serialize(new
        {
            entity.EntityId,
            found = true,
            entity.Confidence,
            entity.UpdatedAtUtc
        });
    }

    [McpServerTool(Name = "memory_create_relationship"), Description("Create a relationship between two entities in the knowledge graph.")]
    public static async Task<string> MemoryCreateRelationship(
        ILongTermMemoryService longTermMemory,
        IIdGenerator idGenerator,
        IClock clock,
        IOptions<AgentMemoryMcpOptions> options,
        [Description("Entity ID of the source entity")] string sourceEntityId,
        [Description("Entity ID of the target entity")] string targetEntityId,
        [Description("Type of relationship (e.g., 'WORKS_FOR', 'LOCATED_IN', 'KNOWS')")] string relationshipType,
        [Description("Description of the relationship (optional)")] string? description = null,
        [Description("Confidence score from 0.0 to 1.0 (optional)")] double? confidence = null,
        [Description("Owner/user identifier (optional). Null = shared/global, visible to every tenant; set it in multi-tenant deployments to scope the relationship to its creator (R1), consistent with the entity/fact/preference create tools.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var relationship = new Relationship
        {
            RelationshipId = idGenerator.GenerateId(),
            SourceEntityId = sourceEntityId,
            TargetEntityId = targetEntityId,
            RelationshipType = relationshipType,
            Description = description,
            OwnerId = userId,
            Confidence = confidence ?? options.Value.DefaultConfidence,
            CreatedAtUtc = clock.UtcNow
        };

        var result = await longTermMemory.AddRelationshipAsync(relationship, cancellationToken).ConfigureAwait(false);
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
