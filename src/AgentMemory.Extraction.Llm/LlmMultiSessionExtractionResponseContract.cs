using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentMemory.Extraction.Llm;

internal static class LlmMultiSessionExtractionResponseContract
{
    internal const string Version = "batch-source-alias-schema-v1";

    internal static string Alias(int zeroBasedIndex) => $"s{zeroBasedIndex + 1}";

    internal static ChatResponseFormat CreateResponseFormat(int sourceSessions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSessions);
        return ChatResponseFormat.ForJsonSchema(
            CreateSchema(sourceSessions),
            "agent_memory_multi_session_v1",
            "Structured memory extracted independently for each source-session alias.");
    }

    internal static JsonElement CreateSchema(int sourceSessions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSessions);
        var aliases = Enumerable.Range(0, sourceSessions).Select(Alias).ToArray();

        Dictionary<string, object?> StringSchema() => new() { ["type"] = "string" };
        Dictionary<string, object?> NullableStringSchema() =>
            new() { ["type"] = new[] { "string", "null" } };
        Dictionary<string, object?> NumberSchema() => new() { ["type"] = "number" };
        Dictionary<string, object?> AliasSchema() => new()
        {
            ["type"] = "string",
            ["enum"] = aliases
        };
        Dictionary<string, object?> ArraySchema(object items) => new()
        {
            ["type"] = "array",
            ["items"] = items
        };
        Dictionary<string, object?> ObjectSchema(
            Dictionary<string, object?> properties,
            params string[] required) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false
        };

        var entity = ObjectSchema(
            new Dictionary<string, object?>
            {
                ["source_session"] = AliasSchema(),
                ["name"] = StringSchema(),
                ["type"] = StringSchema(),
                ["subtype"] = NullableStringSchema(),
                ["description"] = NullableStringSchema(),
                ["confidence"] = NumberSchema(),
                ["aliases"] = ArraySchema(StringSchema())
            },
            "source_session", "name", "type", "subtype", "description", "confidence", "aliases");
        var fact = ObjectSchema(
            new Dictionary<string, object?>
            {
                ["source_session"] = AliasSchema(),
                ["subject"] = StringSchema(),
                ["predicate"] = StringSchema(),
                ["object"] = StringSchema(),
                ["confidence"] = NumberSchema()
            },
            "source_session", "subject", "predicate", "object", "confidence");
        var preference = ObjectSchema(
            new Dictionary<string, object?>
            {
                ["source_session"] = AliasSchema(),
                ["category"] = StringSchema(),
                ["preference"] = StringSchema(),
                ["context"] = NullableStringSchema(),
                ["confidence"] = NumberSchema()
            },
            "source_session", "category", "preference", "context", "confidence");
        var relationship = ObjectSchema(
            new Dictionary<string, object?>
            {
                ["source_session"] = AliasSchema(),
                ["source"] = StringSchema(),
                ["target"] = StringSchema(),
                ["relation_type"] = StringSchema(),
                ["description"] = NullableStringSchema(),
                ["confidence"] = NumberSchema()
            },
            "source_session", "source", "target", "relation_type", "description", "confidence");

        return JsonSerializer.SerializeToElement(
            ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["processed_source_sessions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = AliasSchema(),
                        ["minItems"] = sourceSessions,
                        ["maxItems"] = sourceSessions
                    },
                    ["entities"] = ArraySchema(entity),
                    ["facts"] = ArraySchema(fact),
                    ["preferences"] = ArraySchema(preference),
                    ["relations"] = ArraySchema(relationship)
                },
                "processed_source_sessions", "entities", "facts", "preferences", "relations"));
    }
}
