using System.Buffers;
using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Native.Contract;

/// <summary>Timing of one job, in whole milliseconds.</summary>
internal readonly record struct JobTiming(long QueuedMs, long RunMs);

/// <summary>
/// Writes the result and error documents of the native contract with <see cref="Utf8JsonWriter"/>: no serializer, no
/// reflection. Metadata values are written exactly as they were read from the store.
/// </summary>
internal static class ResultWriter
{
    public const int SchemaVersion = 1;

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            body(writer);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteError(Utf8JsonWriter writer, string code, string message)
    {
        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    }

    private static void WriteHead(Utf8JsonWriter writer, string? operation, string? requestId, string status)
    {
        if (operation is null) writer.WriteNull("operation"); else writer.WriteString("operation", operation);
        if (requestId is null) writer.WriteNull("requestId"); else writer.WriteString("requestId", requestId);
        writer.WriteString("status", status);
    }

    private static void WriteTiming(Utf8JsonWriter writer, JobTiming timing)
    {
        writer.WriteStartObject("timing");
        writer.WriteNumber("queuedMs", timing.QueuedMs);
        writer.WriteNumber("runMs", timing.RunMs);
        writer.WriteEndObject();
    }

    /// <summary>The error document of a failed <c>am_v1_open</c>.</summary>
    public static byte[] OpenError(string code, string message) =>
        Write(writer => WriteError(writer, code, message));

    /// <summary>A job that ended without a usable result: invalid, timeout, cancelled, unavailable or error.</summary>
    public static byte[] Failure(string? operation, string? requestId, string status, string code, string message,
        JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, operation, requestId, status);
            WriteError(writer, code, message);
            WriteTiming(writer, timing);
        });

    public static byte[] Capabilities(string? requestId, OpenConfig config, string libraryVersion, JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, CapabilitiesRequest.Name, requestId, "ok");
            writer.WriteStartObject("library");
            writer.WriteString("name", "agentmemory_native");
            writer.WriteString("version", libraryVersion);
            writer.WriteEndObject();
            writer.WriteStartObject("abi");
            writer.WriteNumber("major", Abi.AbiVersion.Major);
            writer.WriteNumber("minor", Abi.AbiVersion.Minor);
            writer.WriteEndObject();
            writer.WriteString("profile", config.ProfileName);
            writer.WriteStartObject("owner");
            writer.WriteString("mode", config.OwnerMode == OwnerMode.Fixed ? "fixed" : "per_request");
            writer.WriteEndObject();
            writer.WriteStartObject("embedding");
            writer.WriteNumber("dimensions", config.Memory.EmbeddingDimensions);
            if (config.EmbeddingModel is null) writer.WriteNull("model"); else writer.WriteString("model", config.EmbeddingModel);
            writer.WriteBoolean("generatesEmbeddings", config.EmbeddingEndpoint is not null);
            writer.WriteEndObject();
            writer.WriteStartArray("operations");
            foreach (var operation in NativeRequest.Offered(config)) writer.WriteStringValue(operation);
            writer.WriteEndArray();
            // ABI 1.1: what a binding may use beyond ABI 1.0's minimum, so it checks before it sends.
            writer.WriteStartArray("features");
            foreach (var feature in NativeRequest.Features(config)) writer.WriteStringValue(feature);
            writer.WriteEndArray();
            if (config.Writer is { } model)
            {
                // The writer's model is named; its address and key are not.
                writer.WriteStartObject("writer");
                writer.WriteString("model", model.Model);
                writer.WriteBoolean("entityIdentity", false);
                writer.WriteEndObject();
            }
            writer.WriteStartObject("limits");
            writer.WriteNumber("maxRunningJobs", config.MaxRunningJobs);
            writer.WriteNumber("maxPendingJobs", config.MaxPendingJobs);
            writer.WriteNumber("defaultTimeoutMs", config.DefaultTimeoutMs);
            writer.WriteNumber("maxRequestBytes", StrictObject.MaxDocumentBytes);
            if (config.Writes) writer.WriteNumber("maxQueuedWrites", config.MaxQueuedWrites);
            writer.WriteEndObject();
            WriteTiming(writer, timing);
        });

    /// <summary>
    /// <c>schema_status</c> and <c>schema_bootstrap</c> (admin): what the store holds of the schema, after the bootstrap
    /// when <paramref name="bootstrapped"/>.
    /// </summary>
    public static byte[] Schema(string operation, string? requestId, AgentMemory.Neo4j.Infrastructure.SchemaInspection inspection,
        bool bootstrapped, JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, operation, requestId, "ok");
            writer.WriteStartObject("schema");
            writer.WriteBoolean("complete", inspection.Complete);
            writer.WriteBoolean("empty", inspection.Empty);
            writer.WriteNumber("expected", inspection.Expected);
            writer.WriteStartArray("missing");
            foreach (var name in inspection.Missing) writer.WriteStringValue(name);
            writer.WriteEndArray();
            writer.WriteNumber("dimensions", inspection.ConfiguredDimensions);
            writer.WriteBoolean("dimensionsMatch", inspection.DimensionsMatch);
            writer.WriteStartObject("vectorIndexes");
            foreach (var (name, dimensions) in inspection.VectorDimensions.OrderBy(p => p.Key, StringComparer.Ordinal))
                writer.WriteNumber(name, dimensions);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteBoolean("bootstrapped", bootstrapped);
            WriteTiming(writer, timing);
        });

    /// <summary>
    /// The receipt of an explicit write: where it stands, by evidence. <c>duplicate</c> is true when the source was
    /// accepted before and this request ran nothing.
    /// </summary>
    public static byte[] Receipt(string operation, string? requestId, Journal.OperationSnapshot receipt, bool duplicate,
        JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, operation, requestId, "ok");
            writer.WriteStartObject("receipt");
            writer.WriteString("operationId", receipt.OperationId);
            writer.WriteString("kind", receipt.Kind switch
            {
                Journal.OperationKind.Forget => "forget",
                Journal.OperationKind.Erase => "erase",
                _ => "remember",
            });
            writer.WriteString("sourceId", receipt.SourceId);
            if (receipt.Kind == Journal.OperationKind.Remember) writer.WriteString("origin", receipt.Origin);
            WriteOptional(writer, "targetOperationId", receipt.TargetOperationId);
            writer.WriteBoolean("duplicate", duplicate);
            writer.WriteString("state", Journal.WriteCoordinator.Name(receipt.State));
            WriteOptional(writer, "reason", receipt.Reason);
            writer.WriteBoolean("searchable", receipt.Searchable);
            WriteInstant(writer, "occurredAt", receipt.OccurredAt);
            WriteInstant(writer, "acceptedAt", receipt.AcceptedAt);
            WriteInstant(writer, "startedAt", receipt.StartedAt);
            WriteInstant(writer, "finishedAt", receipt.FinishedAt);
            WriteIds(writer, "facts", receipt.Facts);
            WriteIds(writer, "preferences", receipt.Preferences);
            WriteIds(writer, "closed", receipt.Closed);
            if (receipt.Count is { } count) writer.WriteNumber("deleted", count); else writer.WriteNull("deleted");
            writer.WriteEndObject();
            WriteTiming(writer, timing);
        });

    private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(name);
        foreach (var id in ids) writer.WriteStringValue(id);
        writer.WriteEndArray();
    }

    public static byte[] Context(string? requestId, MemoryContext context, JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, GetContextRequest.Name, requestId, "ok");
            writer.WriteStartArray("facts");
            foreach (var fact in context.RelevantFacts.Items) WriteFact(writer, fact);
            writer.WriteEndArray();
            writer.WriteStartArray("preferences");
            foreach (var preference in context.RelevantPreferences.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", preference.PreferenceId);
                writer.WriteString("category", preference.Category);
                writer.WriteString("text", preference.PreferenceText);
                WriteOptional(writer, "context", preference.Context);
                writer.WriteNumber("confidence", preference.Confidence);
                WriteInstant(writer, "createdAt", preference.CreatedAtUtc);
                WriteMetadata(writer, preference.Metadata);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            // The sections a request may ask for (each empty when not asked for): additive to schema version 1.
            writer.WriteStartArray("windowFacts");
            foreach (var fact in context.WindowFacts.Items) WriteFact(writer, fact);
            writer.WriteEndArray();
            writer.WriteStartArray("messages");
            foreach (var message in context.RelevantMessages.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", message.MessageId);
                writer.WriteString("role", message.Role);
                writer.WriteString("text", message.Content);
                WriteInstant(writer, "at", message.TimestampUtc);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("entities");
            foreach (var entity in context.RelevantEntities.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entity.EntityId);
                writer.WriteString("name", entity.Name);
                writer.WriteString("type", entity.Type);
                WriteOptional(writer, "description", entity.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("relationships");
            foreach (var recalled in context.RelevantRelationships.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", recalled.Relationship.RelationshipId);
                writer.WriteString("source", recalled.SourceName);
                writer.WriteString("type", recalled.Relationship.RelationshipType);
                writer.WriteString("target", recalled.TargetName);
                WriteOptional(writer, "description", recalled.Relationship.Description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("truncated", context.Truncated);
            WriteTiming(writer, timing);
        });

    private static void WriteFact(Utf8JsonWriter writer, Fact fact)
    {
        writer.WriteStartObject();
        writer.WriteString("id", fact.FactId);
        writer.WriteString("subject", fact.Subject);
        writer.WriteString("predicate", fact.Predicate);
        writer.WriteString("object", fact.Object);
        writer.WriteNumber("confidence", fact.Confidence);
        WriteOptional(writer, "category", fact.Category);
        WriteInstant(writer, "validFrom", fact.ValidFrom);
        WriteInstant(writer, "validUntil", fact.ValidUntil);
        WriteInstant(writer, "createdAt", fact.CreatedAtUtc);
        WriteMetadata(writer, fact.Metadata);
        writer.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value.Value.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Stored metadata reads back as <see cref="JsonElement"/> values and nulls; those are written as they are. Any
    /// other value would mean a path this profile does not read, so it is refused rather than guessed at.
    /// </summary>
    private static void WriteMetadata(Utf8JsonWriter writer, IReadOnlyDictionary<string, object> metadata)
    {
        writer.WriteStartObject("metadata");
        foreach (var (key, value) in metadata)
        {
            writer.WritePropertyName(key);
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case JsonElement element:
                    element.WriteTo(writer);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Metadata key '{key}' holds a {value.GetType().Name}; the read-only profile returns stored JSON values only.");
            }
        }
        writer.WriteEndObject();
    }
}
