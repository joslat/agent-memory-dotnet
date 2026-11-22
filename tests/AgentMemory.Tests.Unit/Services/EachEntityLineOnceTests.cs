using AgentMemory.Abstractions.Domain;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// IBP-1: where recalled entities are listed for the prompt, entities that would render alike are shown once. A thing
/// stored as several entities (a split) said "Luna (OBJECT)" once per entity, which told the model nothing more; a
/// namesake of another type, or with another description, keeps its own line. Recall's items are not changed.
/// </summary>
public sealed class EachEntityLineOnceTests
{
    private static Entity Entity(string id, string name, string type, string? description = null) => new()
    {
        EntityId = id, Name = name, Type = type, Description = description, Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private static MemoryContext Context(params Entity[] entities) => new()
    {
        SessionId = "s1",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        RelevantEntities = new MemoryContextSection<Entity> { Items = entities },
    };

    private static readonly Entity[] Split =
    [
        Entity("e1", "Luna", "OBJECT"), Entity("e2", "Luna", "OBJECT"), Entity("e3", "luna", "OBJECT"),
        Entity("e4", "Luna", "PERSON"), Entity("e5", "Luna", "OBJECT", "the neighbour's dog"),
    ];

    [Fact]
    public void Alike_entities_are_kept_once_the_first_kept_and_the_order_unchanged()
    {
        MemoryContextFormatter.OncePerLine(Split).Select(e => e.EntityId).Should().Equal("e1", "e4", "e5");
    }

    [Fact]
    public void A_list_without_repeats_is_returned_as_it_is()
    {
        var entities = new[] { Entity("e1", "Luna", "OBJECT"), Entity("e2", "Max", "PERSON") };

        MemoryContextFormatter.OncePerLine(entities).Should().Equal(entities);
    }

    [Fact]
    public void The_core_formatter_lists_each_line_once()
    {
        var text = MemoryContextFormatter.FormatRecallResult(new RecallResult { Context = Context(Split), TotalItemsRetrieved = Split.Length });

        CountOf(text, "- Luna (OBJECT)\n").Should().Be(1);
        CountOf(text, "- luna (OBJECT)").Should().Be(0, "the same name in another case is the same line");
        text.Should().Contain("- Luna (PERSON)").And.Contain("- Luna (OBJECT) — the neighbour's dog");
    }

    [Fact]
    public void The_agent_framework_mapper_lists_each_line_once()
    {
        var text = string.Join("\n", MafTypeMapper.ToContextMessages(Context(Split), new ContextFormatOptions()).Select(m => m.Text));

        CountOf(text, "Luna (OBJECT)").Should().Be(2, "once alone and once with its description");
        text.Should().Contain("Luna (PERSON)").And.Contain("Luna (OBJECT): the neighbour's dog");
        text.Should().NotContain("luna (OBJECT)");
    }

    private static int CountOf(string text, string part)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var count = 0;
        for (var at = normalized.IndexOf(part, StringComparison.Ordinal); at >= 0; at = normalized.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
