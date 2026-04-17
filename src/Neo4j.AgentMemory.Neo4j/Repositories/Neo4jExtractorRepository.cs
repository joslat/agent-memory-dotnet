using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Repositories;

/// <summary>
/// Neo4j implementation of <see cref="IExtractorRepository"/>.
/// </summary>
public sealed class Neo4jExtractorRepository : IExtractorRepository
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly ILogger<Neo4jExtractorRepository> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="Neo4jExtractorRepository"/>.
    /// </summary>
    public Neo4jExtractorRepository(INeo4jTransactionRunner tx, ILogger<Neo4jExtractorRepository> logger)
    {
        _tx = tx;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Extractor> UpsertAsync(Extractor extractor, CancellationToken ct = default)
    {
        _logger.LogDebug("Upserting extractor {Name}", extractor.Name);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.Upsert, new
            {
                id = extractor.ExtractorId,
                name = extractor.Name,
                version = (object?)extractor.Version,
                config = (object?)extractor.ConfigJson
            });
            var records = await cursor.ToListAsync();
            if (records.Count > 0)
                return MapToExtractor(records[0]["ex"].As<INode>());
            return extractor;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<Extractor?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting extractor by name {Name}", name);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.GetByName, new { name });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;
            return MapToExtractor(records[0]["ex"].As<INode>());
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Extractor>> ListAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Listing all extractors");

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.List, new { });
            var records = await cursor.ToListAsync();
            return records.Select(r => MapToExtractor(r["ex"].As<INode>())).ToList();
        }, ct);
    }

    /// <inheritdoc />
    public async Task CreateExtractedByRelationshipAsync(
        string entityId,
        string extractorName,
        double confidence,
        int? extractionTimeMs = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Creating EXTRACTED_BY: Entity {EntityId} -> Extractor {Extractor}", entityId, extractorName);

        await _tx.WriteAsync(async runner =>
        {
            await runner.RunAsync(ExtractorQueries.CreateExtractedByRelationship, new
            {
                entity_id = entityId,
                extractor_name = extractorName,
                confidence,
                extraction_time_ms = (object?)extractionTimeMs
            });
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Entity Entity, double Confidence)>> GetEntitiesByExtractorAsync(
        string extractorName,
        int limit = 100,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Getting entities by extractor {Extractor}, limit={Limit}", extractorName, limit);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.GetEntitiesByExtractor, new { extractor_name = extractorName, limit });
            var records = await cursor.ToListAsync();
            return records.Select(r =>
            {
                var node = r["e"].As<INode>();
                var conf = r["confidence"].As<double>();
                return (MapToEntity(node), conf);
            }).ToList();
        }, ct);
    }

    /// <inheritdoc />
    public async Task<EntityProvenance?> GetProvenanceAsync(string entityId, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting provenance for entity {EntityId}", entityId);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.GetEntityProvenance, new { entityId });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;

            var record = records[0];
            var id = record["entityId"]?.As<string>();
            if (id is null) return null;

            var sources = record["sources"].As<IList<object>>()
                .Cast<IDictionary<string, object>>()
                .Where(d => d["messageId"] is not null)
                .Select(d => new ProvenanceSource(
                    d["messageId"].As<string>(),
                    d.TryGetValue("confidence", out var c) && c is not null ? c.As<double>() : null,
                    d.TryGetValue("startPos", out var sp) && sp is not null ? sp.As<int>() : null,
                    d.TryGetValue("endPos", out var ep) && ep is not null ? ep.As<int>() : null))
                .ToList();

            var extractors = record["extractors"].As<IList<object>>()
                .Cast<IDictionary<string, object>>()
                .Where(d => d["extractorName"] is not null)
                .Select(d => new ProvenanceExtractor(
                    d["extractorName"].As<string>(),
                    d.TryGetValue("confidence", out var c) && c is not null ? c.As<double>() : 0.0,
                    d.TryGetValue("extractionTimeMs", out var t) && t is not null ? t.As<int>() : null))
                .ToList();

            return new EntityProvenance(id, sources, extractors);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<ExtractionStats> GetExtractionStatsAsync(CancellationToken ct = default)
    {
        _logger.LogDebug("Getting extraction stats");

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.GetExtractionStats, new { });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return new ExtractionStats(0, 0, 0.0);

            var record = records[0];
            return new ExtractionStats(
                record["totalEntities"].As<int>(),
                record["totalMessages"].As<int>(),
                record["avgPerMessage"].As<double>());
        }, ct);
    }

    /// <inheritdoc />
    public async Task<ExtractorStats?> GetExtractorStatsAsync(string extractorName, CancellationToken ct = default)
    {
        _logger.LogDebug("Getting stats for extractor {Extractor}", extractorName);

        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.GetExtractorStats, new { extractorName });
            var records = await cursor.ToListAsync();
            if (records.Count == 0) return null;

            var record = records[0];
            var name = record["name"]?.As<string>();
            if (name is null) return null;

            return new ExtractorStats(
                name,
                record["entityCount"].As<int>(),
                record["avgConfidence"] is not null ? record["avgConfidence"].As<double>() : 0.0,
                record["totalExtractions"].As<int>());
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> DeleteProvenanceAsync(string entityId, CancellationToken ct = default)
    {
        _logger.LogDebug("Deleting provenance for entity {EntityId}", entityId);

        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(ExtractorQueries.DeleteEntityProvenance, new { entityId });
            var records = await cursor.ToListAsync();
            return records.Count > 0 ? records[0]["deleted"].As<int>() : 0;
        }, ct);
    }

    private static Extractor MapToExtractor(INode node)
    {
        return new Extractor
        {
            ExtractorId = node["id"].As<string>(),
            Name = node["name"].As<string>(),
            Version = node.Properties.TryGetValue("version", out var v) ? v?.As<string>() : null,
            ConfigJson = node.Properties.TryGetValue("config", out var c) ? c?.As<string>() : null,
            CreatedAtUtc = node.Properties.TryGetValue("created_at", out var ca) && ca is not null
                ? DateTimeOffset.Parse(ca.As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTimeOffset.UtcNow
        };
    }

    private static Entity MapToEntity(INode node)
    {
        double? latitude = null;
        double? longitude = null;
        if (node.Properties.TryGetValue("location", out var locValue) && locValue is Point pt)
        {
            latitude = pt.Y;
            longitude = pt.X;
        }

        return new Entity
        {
            EntityId = node["id"].As<string>(),
            Name = node["name"].As<string>(),
            CanonicalName = node.Properties.TryGetValue("canonical_name", out var cn) ? cn.As<string>() : null,
            Type = node["type"].As<string>(),
            Subtype = node.Properties.TryGetValue("subtype", out var st) ? st.As<string>() : null,
            Description = node.Properties.TryGetValue("description", out var desc) ? desc.As<string>() : null,
            Confidence = node["confidence"].As<double>(),
            Latitude = latitude,
            Longitude = longitude,
            Aliases = node.Properties.TryGetValue("aliases", out var al)
                ? al.As<IList<object>>().Select(a => a.ToString()!).ToList()
                : Array.Empty<string>(),
            Attributes = DeserializeMetadata(node.Properties.TryGetValue("attributes", out var attr) ? attr.As<string>() : null),
            SourceMessageIds = node.Properties.TryGetValue("source_message_ids", out var sm)
                ? sm.As<IList<object>>().Select(v => v.ToString()!).ToList()
                : Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.Parse(node["created_at"].As<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Metadata = DeserializeMetadata(node.Properties.TryGetValue("metadata", out var md) ? md.As<string>() : null)
        };
    }

    private static IReadOnlyDictionary<string, object> DeserializeMetadata(string? json)
        => string.IsNullOrEmpty(json)
            ? new Dictionary<string, object>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json)
              ?? new Dictionary<string, object>();
}
