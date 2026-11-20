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

    /// <summary>Metadata as hosts and the library write it: every listed value type, nested, and values read back.</summary>
    public static TheoryData<string, IReadOnlyDictionary<string, object>> WrittenMetadata()
    {
        var stored = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"o":{"x":[1,2.5,"y",null,true]},"n":18446744073709551615,"s":"Zürich ✓ 😀"}""")!;
        return new()
        {
            { "text", new Dictionary<string, object> { ["source"] = "write-probe", ["s"] = "Zürich ✓ 😀 <b>&\"'\\" } },
            { "numbers", new Dictionary<string, object>
                {
                    ["b"] = (byte)7, ["h"] = (short)-3, ["i"] = 42, ["l"] = long.MaxValue, ["f"] = 0.1f, ["d"] = 0.1,
                    ["m"] = 12.50m, ["nan-free"] = double.Epsilon,
                } },
            { "booleans and times", new Dictionary<string, object>
                {
                    ["t"] = true, ["at"] = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(1)),
                    ["utc"] = new DateTime(2026, 11, 20, 9, 0, 0, DateTimeKind.Utc), ["id"] = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"),
                } },
            { "lists", new Dictionary<string, object>
                {
                    ["ids"] = new[] { "f1", "f2" }, ["ints"] = new[] { 1, 2 }, ["longs"] = new[] { 1L }, ["ds"] = new[] { 1.5 },
                    ["flags"] = new[] { true, false }, ["mixed"] = new object[] { "a", 1, true, 2.5 },
                    ["list"] = new List<object> { "a", 2 }, ["names"] = new List<string> { "x" },
                } },
            { "nested", new Dictionary<string, object>
                {
                    ["inner"] = new Dictionary<string, object> { ["depth"] = 2, ["label"] = "Zürich ✓", ["deeper"] = new Dictionary<string, object> { ["k"] = "v" } },
                    ["rows"] = new List<Dictionary<string, object>> { new() { ["a"] = 1 } },
                } },
            { "values read back from the store", stored },
        };
    }

    [Theory]
    [MemberData(nameof(WrittenMetadata))]
    public void The_generated_contract_writes_metadata_with_the_same_bytes_as_the_reflection_call(
        string name, IReadOnlyDictionary<string, object> metadata) =>
        JsonSerializer.Serialize(metadata, Neo4jMetadataJsonContext.Default.IReadOnlyDictionaryStringObject)
            .Should().Be(JsonSerializer.Serialize(metadata), name);

    private sealed record Unlisted(string Name);

    [Fact]
    public void The_generated_contract_refuses_a_value_type_it_does_not_list_instead_of_writing_it_differently() =>
        FluentActions.Invoking(() => JsonSerializer.Serialize(
                (IReadOnlyDictionary<string, object>)new Dictionary<string, object> { ["x"] = new Unlisted("n") },
                Neo4jMetadataJsonContext.Default.IReadOnlyDictionaryStringObject))
            .Should().Throw<NotSupportedException>();
}
