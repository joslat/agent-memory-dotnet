using System.Text.Json;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// The writer's reply schema is parsed without the serializer's reflection path (a host with reflection-based JSON
/// off builds it too); its shape is unchanged.
/// </summary>
public sealed class ReplyFormatSchemaTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    public void The_writer_schema_bounds_operations_and_requires_every_field(int requested, int expected)
    {
        var format = MemoryWriterContract.Format(requested).Should().BeOfType<ChatResponseFormatJson>().Which;
        var schema = format.Schema!.Value;

        format.SchemaName.Should().Be("agent_memory_writer_ops_v1");
        schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        var ops = schema.GetProperty("properties").GetProperty("ops");
        ops.GetProperty("maxItems").GetInt32().Should().Be(expected);
        ops.GetProperty("items").GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("op", "kind", "text", "quote", "id", "until", "ref");
        schema.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
