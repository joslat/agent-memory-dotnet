using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed memory history reader. It is intentionally read-only and schema-neutral: it projects the
/// existing lifecycle fields (<c>invalidated_at</c>, <c>valid_until</c>, <c>:SUPERSEDED_BY</c>, provenance)
/// plus read-audit/access fields into a normalized API/CLI surface.
/// </summary>
public sealed class Neo4jMemoryHistoryService(INeo4jTransactionRunner tx) : IMemoryHistoryService
{
    public async Task<IReadOnlyList<MemoryHistoryRecord>> GetHistoryAsync(
        MemoryHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new Dictionary<string, object?>
        {
            ["id"] = NullIfWhiteSpace(query.Id),
            ["ownerId"] = NullIfWhiteSpace(query.OwnerId),
            ["includeShared"] = query.IncludeShared,
            ["includeInvalidated"] = query.IncludeInvalidated,
            ["limit"] = ClampLimit(query.Limit),
        };

        return await tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(HistoryQueries.List(query.Kind), parameters).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<MemoryHistoryRecord>)records.Select(Map).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static MemoryHistoryRecord Map(global::Neo4j.Driver.IRecord record)
    {
        var invalidatedAt = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["invalidatedAt"]);
        var kindText = ReadRequiredString(record["kind"]);

        return new MemoryHistoryRecord
        {
            Kind = Enum.Parse<MemoryHistoryKind>(kindText, ignoreCase: true),
            Id = ReadRequiredString(record["id"]),
            Summary = ReadRequiredString(record["summary"]),
            OwnerId = ReadOptionalString(record["ownerId"]),
            Status = invalidatedAt is null ? MemoryHistoryStatus.Live : MemoryHistoryStatus.Invalidated,
            CreatedAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["createdAt"]) ?? DateTimeOffset.MinValue,
            UpdatedAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["updatedAt"]),
            InvalidatedAtUtc = invalidatedAt,
            LastAccessedAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["lastAccessedAt"]),
            AccessCount = ReadOptionalInt(record["accessCount"]),
            ReadAuditCount = ReadOptionalInt(record["readAuditCount"]),
            LastReadAuditAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["lastReadAuditAt"]),
            ValidFromUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["validFrom"]),
            ValidUntilUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(record["validUntil"]),
            SourceMessageIds = ReadStringList(record["sourceMessageIds"]),
            SupersededByIds = ReadStringList(record["supersededByIds"]),
            SupersedesIds = ReadStringList(record["supersedesIds"]),
            Metadata = Neo4jRecordMapper.DeserializeMetadata(record["metadata"]?.ToString()),
        };
    }

    private static int ClampLimit(int limit) => Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadOptionalString(object? value)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string ReadRequiredString(object? value) => value?.ToString() ?? string.Empty;

    private static int ReadOptionalInt(object? value)
    {
        if (value is null) return 0;
        try
        {
            return Convert.ToInt32(value);
        }
        catch (FormatException)
        {
            return 0;
        }
        catch (InvalidCastException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> ReadStringList(object? value)
    {
        if (value is null) return Array.Empty<string>();
        if (value is string s) return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : new[] { s };

        if (value is System.Collections.IEnumerable enumerable)
        {
            return enumerable
                .Cast<object?>()
                .Select(v => v?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text! };
    }
}
