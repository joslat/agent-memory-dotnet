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
            writer.WriteString("profile", "read_only");
            writer.WriteStartObject("owner");
            writer.WriteString("mode", config.OwnerMode == OwnerMode.Fixed ? "fixed" : "per_request");
            writer.WriteEndObject();
            writer.WriteStartObject("embedding");
            writer.WriteNumber("dimensions", config.Memory.EmbeddingDimensions);
            if (config.EmbeddingModel is null) writer.WriteNull("model"); else writer.WriteString("model", config.EmbeddingModel);
            writer.WriteBoolean("generatesEmbeddings", false);
            writer.WriteEndObject();
            writer.WriteStartArray("operations");
            writer.WriteStringValue(CapabilitiesRequest.Name);
            writer.WriteStringValue(GetContextRequest.Name);
            writer.WriteEndArray();
            writer.WriteStartObject("limits");
            writer.WriteNumber("maxRunningJobs", config.MaxRunningJobs);
            writer.WriteNumber("maxPendingJobs", config.MaxPendingJobs);
            writer.WriteNumber("defaultTimeoutMs", config.DefaultTimeoutMs);
            writer.WriteNumber("maxRequestBytes", StrictObject.MaxDocumentBytes);
            writer.WriteEndObject();
            WriteTiming(writer, timing);
        });

    public static byte[] Context(string? requestId, MemoryContext context, JobTiming timing) =>
        Write(writer =>
        {
            WriteHead(writer, GetContextRequest.Name, requestId, "ok");
            writer.WriteStartArray("facts");
            foreach (var fact in context.RelevantFacts.Items)
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
            writer.WriteBoolean("truncated", context.Truncated);
            WriteTiming(writer, timing);
        });

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
