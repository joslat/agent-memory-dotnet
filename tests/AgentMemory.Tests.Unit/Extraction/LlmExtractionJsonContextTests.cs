using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentMemory.Extraction.Llm.Internal;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The extraction reply read through generated metadata (a host with reflection-based JSON off: the native library)
/// gives what the reflection path gives managed hosts: the same objects for the same replies, and for a reply that
/// cannot be read, the same exception, path and message (the salvage pass reports them).
/// </summary>
public sealed class LlmExtractionJsonContextTests
{
    private static readonly JsonSerializerOptions Reflection = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string, string> Replies() => new()
    {
        { "complete", """
            {"processed_source_sessions":["s1","s2"],
             "entities":[{"name":"Mochi","type":"ANIMAL","subtype":"dog","description":"the user's dog","confidence":0.7,"aliases":["Mo"],"source_session":"s1"}],
             "facts":[{"subject":"user","predicate":"works_in","object":"Zurich","confidence":0.95,"category":"work",
                       "valid_from":"2024-03","valid_until":"2026","occurred_on":"2026-11-20","source_role":"user","source_turn":4,"replaces":"f-1"}],
             "preferences":[{"category":"style","preference":"Prefers concise answers","context":"voice","confidence":0.9,"source_role":"user","replaces":null,"source_turn":2}],
             "relations":[{"source":"user","target":"Mochi","relation_type":"OWNS","description":null,"confidence":0.8}]}
            """ },
        { "case and defaults", """{"FACTS":[{"Subject":"user","PREDICATE":"likes","object":"ramen"}],"Entities":[{"name":"Zurich","type":"PLACE"}]}""" },
        { "empty object", "{}" },
        { "null lists and control field", """{"entities":null,"facts":[],"processed_source_sessions":null}""" },
        { "dates the converter drops or reads", """
            {"facts":[{"subject":"a","predicate":"b","object":"c","valid_from":"someday","valid_until":true,"occurred_on":2024},
                      {"subject":"d","predicate":"e","object":"f","valid_from":null,"valid_until":"ongoing"}]}
            """ },
        { "unknown fields", """{"facts":[{"subject":"a","predicate":"b","object":"c","mood":"happy"}],"extra":{"x":[1,2]}}""" },
    };

    public static TheoryData<string, string> Unreadable() => new()
    {
        { "a number as text", """{"facts":[{"subject":"a","predicate":"b","object":"c","confidence":"high"}]}""" },
        { "an object for a list", """{"entities":{"name":"x"}}""" },
        { "a string for a turn", """{"preferences":[{"category":"c","preference":"p","source_turn":"two"}]}""" },
        { "a list of numbers for sessions", """{"processed_source_sessions":[1,2]}""" },
    };

    [Theory]
    [MemberData(nameof(Replies))]
    public void A_reply_reads_the_same_through_generated_metadata(string name, string json)
    {
        var reflected = JsonSerializer.Deserialize<LlmExtractionResponse>(json, Reflection);
        var generated = JsonSerializer.Deserialize(json, LlmExtractionJsonContext.Default.LlmExtractionResponse);

        generated.Should().BeEquivalentTo(reflected, o => o.ComparingByMembers<LlmExtractionResponse>(), name);
    }

    [Theory]
    [MemberData(nameof(Replies))]
    public void Each_item_reads_the_same_through_generated_metadata_for_the_salvage_pass(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var element in property.Value.EnumerateArray())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "entities": Same(element, LlmExtractionJsonContext.Default.LlmEntityDto, name); break;
                    case "facts": Same(element, LlmExtractionJsonContext.Default.LlmFactDto, name); break;
                    case "preferences": Same(element, LlmExtractionJsonContext.Default.LlmPreferenceDto, name); break;
                    case "relations": Same(element, LlmExtractionJsonContext.Default.LlmRelationshipDto, name); break;
                    case "processed_source_sessions": break;
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void An_unreadable_reply_fails_the_same_way_through_generated_metadata(string name, string json)
    {
        var reflected = Capture(() => JsonSerializer.Deserialize<LlmExtractionResponse>(json, Reflection));
        var generated = Capture(() => JsonSerializer.Deserialize(json, LlmExtractionJsonContext.Default.LlmExtractionResponse));

        reflected.Should().NotBeNull(name);
        generated.Should().NotBeNull(name);
        generated!.GetType().Should().Be(reflected!.GetType(), name);
        (generated as JsonException)?.Path.Should().Be((reflected as JsonException)?.Path, name);
        generated.Message.Should().Be(reflected.Message, name);
    }

    private static void Same<T>(JsonElement element, JsonTypeInfo<T> generatedInfo, string name)
    {
        var reflected = element.Deserialize<T>(Reflection);
        var generated = element.Deserialize(generatedInfo);
        generated.Should().BeEquivalentTo(reflected, o => o.ComparingByMembers<T>(), name);
    }

    private static Exception? Capture(Action read)
    {
        try
        {
            read();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
