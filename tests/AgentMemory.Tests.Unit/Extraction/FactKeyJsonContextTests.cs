using System.Text.Json;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The canonical fact key written through generated metadata (a host with reflection-based JSON off: the native
/// library) has the same bytes as through reflection, for every kind of text a model can return.
/// </summary>
public sealed class FactKeyJsonContextTests
{
    public static TheoryData<string, string, string> Triples() => new()
    {
        { "user", "works_in", "Zurich" },
        { "user", "lives in", "Zürich, Schweiz" },
        { "O'Brien \"the elder\"", "said", "a\\b/c" },
        { "<b>Tom & Jerry</b>", "likes", "+1 `code`" },
        { "user", "feels", "\U0001F600 and été" },
        { "tab\there", "line\nbreak", "bell\u0007" },
        { "lone \ud800 surrogate", "x", "y" },
        { "", "", "" },
    };

    [Theory]
    [MemberData(nameof(Triples))]
    public void The_key_has_the_same_bytes_through_generated_metadata(string subject, string predicate, string obj)
    {
        string[] key = [subject, predicate, obj];

        JsonSerializer.Serialize(key, FactKeyJsonContext.Default.StringArray).Should().Be(JsonSerializer.Serialize(key));
    }
}
