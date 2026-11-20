using System.Text.Json;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Pins how persisted metadata JSON is read back (design cases J-C01 to J-C05), so a change to the reader cannot move
/// what existing stored records mean. Every case is a JSON string as it sits on a Neo4j node.
/// </summary>
public sealed class Neo4jMetadataJsonCompatibilityTests
{
    /// <summary>One read result as comparable data: key, then JSON kind and raw text, or "clr-null".</summary>
    internal static IReadOnlyList<string> Describe(IReadOnlyDictionary<string, object> metadata) =>
        metadata.Select(pair => pair.Value switch
        {
            null => $"{pair.Key}=clr-null",
            JsonElement element => $"{pair.Key}={element.ValueKind}:{element.GetRawText()}",
            var other => $"{pair.Key}=clr:{other.GetType().FullName}",
        }).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Blank_empty_and_null_documents_read_as_an_empty_dictionary(string? json) =>
        Neo4jRecordMapper.DeserializeMetadata(json).Should().BeEmpty();

    [Fact]
    public void A_present_null_is_a_present_key_with_a_clr_null_value() =>
        Describe(Neo4jRecordMapper.DeserializeMetadata("""{"a":null,"b":1}"""))
            .Should().Equal("a=clr-null", "b=Number:1");

    [Fact]
    public void A_repeated_key_keeps_its_last_value_and_keys_are_case_sensitive() =>
        Describe(Neo4jRecordMapper.DeserializeMetadata("""{"a":1,"A":2,"a":3}"""))
            .Should().Equal("a=Number:3", "A=Number:2");

    [Fact]
    public void Numbers_keep_their_exact_lexeme() =>
        Describe(Neo4jRecordMapper.DeserializeMetadata(
                """{"u":18446744073709551615,"d":0.1000000000000000055511151231257827,"e":1e400,"n":-0}"""))
            .Should().Equal(
                "u=Number:18446744073709551615",
                "d=Number:0.1000000000000000055511151231257827",
                "e=Number:1e400",
                "n=Number:-0");

    [Fact]
    public void Nested_values_stay_json_elements_with_their_raw_text() =>
        Describe(Neo4jRecordMapper.DeserializeMetadata(
                """{"o":{"x":[1,2.5,"y",null,true]},"a":[],"t":true,"f":false,"s":"text"}"""))
            .Should().Equal(
                """o=Object:{"x":[1,2.5,"y",null,true]}""",
                "a=Array:[]",
                "t=True:true",
                "f=False:false",
                "s=String:\"text\"");

    [Fact]
    public void Escaped_unicode_reads_as_the_same_characters()
    {
        var metadata = Neo4jRecordMapper.DeserializeMetadata("""{"s":"Zürich ✓ 😀"}""");
        ((JsonElement)metadata["s"]).GetString().Should().Be("Zürich ✓ 😀");
    }

    [Fact]
    public void Values_stay_readable_after_the_call_returns()
    {
        var metadata = Neo4jRecordMapper.DeserializeMetadata("""{"o":{"deep":{"k":"v"}}}""");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        ((JsonElement)metadata["o"]).GetProperty("deep").GetProperty("k").GetString().Should().Be("v");
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""/*c*/{"a":1}""")]
    [InlineData("""{"a":1} x""")]
    public void Malformed_or_non_object_documents_throw_a_json_exception(string json) =>
        FluentActions.Invoking(() => Neo4jRecordMapper.DeserializeMetadata(json)).Should().Throw<JsonException>();

    [Fact]
    public void Nesting_deeper_than_64_levels_throws()
    {
        var json = "{\"a\":" + new string('[', 64) + new string(']', 64) + "}";
        FluentActions.Invoking(() => Neo4jRecordMapper.DeserializeMetadata(json)).Should().Throw<JsonException>();
    }

    /// <summary>Every well-formed stored document of the fixtures above, plus the shapes the SDK writes.</summary>
    public static TheoryData<string> StoredDocuments => new()
    {
        "{}",
        "null",
        """{"a":null,"b":1}""",
        """{"a":1,"A":2,"a":3}""",
        """{"u":18446744073709551615,"d":0.1000000000000000055511151231257827,"e":1e400,"n":-0}""",
        """{"o":{"x":[1,2.5,"y",null,true]},"a":[],"t":true,"f":false,"s":"text"}""",
        """{"s":"Zürich ✓ 😀"}""",
        """{"trust_level":"verified","kind":"derived","input_fact_ids":["f1","f2"],"bytes":"AQID","at":"2026-11-20T10:00:00+01:00"}""",
    };

    [Theory]
    [MemberData(nameof(StoredDocuments))]
    public void The_generated_contract_reads_stored_metadata_exactly_like_the_reflection_call(string json)
    {
        var reflection = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        var generated = JsonSerializer.Deserialize(json, Neo4jMetadataJsonContext.Default.DictionaryStringObject)
                        ?? new Dictionary<string, object>();

        Describe(generated).Should().Equal(Describe(reflection));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""/*c*/{"a":1}""")]
    public void The_generated_contract_refuses_what_the_reflection_call_refuses(string json) =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize(json, Neo4jMetadataJsonContext.Default.DictionaryStringObject))
            .Should().Throw<JsonException>();
}
