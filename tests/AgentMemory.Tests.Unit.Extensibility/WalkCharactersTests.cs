#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Feature 02: the walk counts by character (a code point), as the format does, so a second implementation in another language
/// reaches the same anchors, matches and reasons: a glob's '?' is one character, a reason gives one '_' a character, and the
/// intake's bounds count characters.
/// </summary>
public sealed class WalkCharactersTests
{
    [Fact]
    public void A_glob_question_mark_is_one_character_an_emoji_included()
    {
        long work = 0;
        AnchorMatcher.GlobPattern.Of("src/?.cs").Matches("src/\U0001F600.cs", ref work).Should().BeTrue("one emoji is one character");
        AnchorMatcher.GlobPattern.Of("src/??.cs").Matches("src/\U0001F600.cs", ref work).Should().BeFalse("its two UTF-16 units are not two characters");
        AnchorMatcher.GlobPattern.Of("src/\U0001F600*").Matches("src/\U0001F600-a.cs", ref work).Should().BeTrue("a literal emoji matches itself");
    }

    [Fact]
    public void A_reason_gives_one_underscore_a_character()
    {
        TraversalWalk.Strict("a\U0001F600b").Should().Be("a_b");
        TraversalWalk.Strict("café").Should().Be("caf_");
    }

    [Fact]
    public void The_intake_counts_characters()
    {
        // 512 emoji are 1,024 UTF-16 units and within the 512-character bound; 513 are not.
        var at = ContextAnchor.Path("src/" + string.Concat(Enumerable.Repeat("\U0001F600", 508)));
        var over = ContextAnchor.Path("src/" + string.Concat(Enumerable.Repeat("\U0001F600", 509)));

        TraversalWalk.Anchors([at, over]).Select(a => a.Value).Should().Equal(at.Value);
    }

    [Fact]
    public void Tied_records_are_in_the_format_s_order_by_code_point()
    {
        // U+FF61 sorts before U+10000 as a code point, after it in .NET's ordinal (UTF-16) order.
        static ModuleRecord Record(string key) => new()
        {
            ModuleId = "com.example.walk", Type = "WalkItem", Id = "id-" + key, Key = key, CreatedAtUtc = DateTimeOffset.UnixEpoch,
            SourceSessionId = "pack:com.example.walk", Source = new() { Excerpt = "records.jsonl:1" },
        };

        new[] { Record("\U00010000"), Record("\uFF61"), Record("a") }.Order(TraversalOrder.Records).Select(r => r.Key)
            .Should().Equal("a", "\uFF61", "\U00010000");
    }
}
