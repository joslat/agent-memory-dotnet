using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// A memory block a human can actually read (S4), and the two things it must never become.
/// </summary>
/// <remarks>
/// <para>
/// The honest assessment of this store was <i>"capable but opaque"</i>: memory could be queried but
/// not <i>seen</i>. This closes that gap — while refusing the part of the block-memory design that
/// makes the block itself the store.
/// </para>
/// <para>
/// So the two properties under test beyond formatting are: a block never presents a retracted claim
/// as current, and it never ends silently when there was more to show. Both failures look exactly
/// like a correct block to the person reading it.
/// </para>
/// </remarks>
public sealed class MemoryBlockRendererTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static Entity E(string id, string name, string type = "Person", double confidence = 0.9) => new()
    {
        EntityId = id, Name = name, Type = type, Confidence = confidence,
        CreatedAtUtc = At, OwnerId = "alice",
    };

    private static Fact F(
        string id, string subject, string predicate, string @object,
        double confidence = 0.9, DateTimeOffset? invalidatedAt = null) => new()
    {
        FactId = id, Subject = subject, Predicate = predicate, Object = @object,
        Confidence = confidence, CreatedAtUtc = At, OwnerId = "alice",
        InvalidatedAtUtc = invalidatedAt,
    };

    private static Preference P(string id, string category, string text, double confidence = 0.9) => new()
    {
        PreferenceId = id, Category = category, PreferenceText = text,
        Confidence = confidence, CreatedAtUtc = At, OwnerId = "alice",
    };

    private static MemoryBlock Render(
        Entity[]? entities = null, Fact[]? facts = null, Preference[]? preferences = null, int maxLines = 50) =>
        MemoryBlockRenderer.Render(
            entities ?? [], facts ?? [], preferences ?? [], At, "alice", maxLines);

    // ── what a block must never do ────────────────────────────────────────

    [Fact]
    public void ASupersededFactIsNotShown()
    {
        // A block says what memory believes NOW, and shows no per-item history. A retracted claim
        // sitting beside a live one is simply presented as true.
        var block = Render(facts:
        [
            F("f-1", "Alice", "lives in", "Basel", invalidatedAt: At),
            F("f-2", "Alice", "lives in", "Zurich"),
        ]);

        block.Lines.Should().ContainSingle().Which.MemoryId.Should().Be("f-2");
    }

    [Fact]
    public void TruncationIsCountedRatherThanSilent()
    {
        // THE failure mode of any bounded view. A block that quietly stops short reads as "this is
        // everything", and invites the conclusion that a missing memory was never stored at all.
        var facts = Enumerable.Range(1, 10)
            .Select(i => F($"f-{i}", "Alice", "likes", $"thing-{i}")).ToArray();

        var block = Render(facts: facts, maxLines: 4);

        block.Lines.Should().HaveCount(4);
        block.OmittedCount.Should().Be(6);
        block.IsTruncated.Should().BeTrue();
    }

    [Fact]
    public void TruncationIsVisibleInTheTextToo()
    {
        // The object carries OmittedCount; the person reading the rendered block sees only the text.
        var facts = Enumerable.Range(1, 10)
            .Select(i => F($"f-{i}", "Alice", "likes", $"thing-{i}")).ToArray();

        MemoryBlockRenderer.ToText(Render(facts: facts, maxLines: 4))
            .Should().Contain("6 more not shown");
    }

    // ── what makes it actionable ──────────────────────────────────────────

    [Fact]
    public void EveryLineCarriesItsMemoryId()
    {
        // The reason this could stay a read surface. A human who spots something wrong acts on that
        // exact memory through the audited write path, instead of editing prose and hoping something
        // parses it back.
        var block = Render(
            entities: [E("e-1", "Alice")],
            facts: [F("f-1", "Alice", "lives in", "Zurich")],
            preferences: [P("p-1", "food", "vegetarian")]);

        block.Lines.Select(l => l.MemoryId).Should().Equal("e-1", "f-1", "p-1");
        MemoryBlockRenderer.ToText(block).Should().Contain("`f-1`");
    }

    [Fact]
    public void KindsAreGroupedUnderHeadings()
    {
        var text = MemoryBlockRenderer.ToText(Render(
            entities: [E("e-1", "Alice")],
            facts: [F("f-1", "Alice", "lives in", "Zurich")]));

        text.Should().Contain("## Entity").And.Contain("## Fact");
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        // Two renders of the same memory produce the same bytes, so a developer diffing blocks sees
        // what changed in memory rather than what changed in the renderer.
        Fact[] facts = [F("f-2", "Alice", "works at", "Acme"), F("f-1", "Alice", "lives in", "Zurich")];

        MemoryBlockRenderer.ToText(Render(facts: facts))
            .Should().Be(MemoryBlockRenderer.ToText(Render(facts: facts.AsEnumerable().Reverse().ToArray())));
    }

    [Fact]
    public void MoreConfidentMemoriesComeFirst()
    {
        var block = Render(facts:
        [
            F("f-low", "Alice", "maybe likes", "sailing", confidence: 0.55),
            F("f-high", "Alice", "lives in", "Zurich", confidence: 0.99),
        ]);

        block.Lines.First().MemoryId.Should().Be("f-high");
    }

    // ── shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyMemorySaysSoRatherThanRenderingNothing()
    {
        // A blank block is indistinguishable from a broken renderer.
        var text = MemoryBlockRenderer.ToText(Render());

        text.Should().Contain("holds nothing");
    }

    [Fact]
    public void TheBlockDeclaresItIsASnapshot()
    {
        // It is rendered on demand and never stored. Saying so in the text is what stops a reader
        // treating it as a document to edit -- which is exactly how a block quietly becomes the store.
        MemoryBlockRenderer.ToText(Render(entities: [E("e-1", "Alice")]))
            .Should().Contain("snapshot, not a document to edit");
    }

    [Fact]
    public void AZeroLineBudgetIsRejected()
    {
        // Not silently treated as unlimited or as empty: both produce a block that misrepresents
        // memory, in opposite directions.
        var act = () => Render(maxLines: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

/// <summary>
/// Rendering a block from the memory-history rows the CLI actually feeds it (S4).
/// </summary>
/// <remarks>
/// The typed overload is precise; this is the one a person reaches through <c>agentmemory block</c>,
/// and it is where a mistake would be seen rather than merely possible.
/// </remarks>
public sealed class MemoryBlockFromHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static MemoryHistoryRecord R(
        string id, MemoryHistoryKind kind, string summary,
        MemoryHistoryStatus status = MemoryHistoryStatus.Live,
        DateTimeOffset? invalidatedAt = null) => new()
    {
        Id = id,
        Kind = kind,
        Summary = summary,
        Status = status,
        CreatedAtUtc = At,
        InvalidatedAtUtc = invalidatedAt,
        OwnerId = "alice",
    };

    [Fact]
    public void InvalidatedRowsAreDroppedEvenIfTheQueryAskedForThem()
    {
        // Belt and braces, deliberately. A caller forgetting IncludeInvalidated = false would get a
        // block presenting retracted claims as current, and nothing about it would look wrong.
        var block = MemoryBlockRenderer.Render(
        [
            R("f-1", MemoryHistoryKind.Fact, "Alice lives in Basel",
                status: MemoryHistoryStatus.Invalidated, invalidatedAt: At),
            R("f-2", MemoryHistoryKind.Fact, "Alice lives in Zurich"),
        ], At, "alice");

        block.Lines.Should().ContainSingle().Which.MemoryId.Should().Be("f-2");
    }

    [Fact]
    public void KindsAreGroupedEntitiesThenFactsThenPreferences()
    {
        var block = MemoryBlockRenderer.Render(
        [
            R("p-1", MemoryHistoryKind.Preference, "food: vegetarian"),
            R("f-1", MemoryHistoryKind.Fact, "Alice lives in Zurich"),
            R("e-1", MemoryHistoryKind.Entity, "Alice"),
        ], At, "alice");

        block.Lines.Select(l => l.MemoryId).Should().Equal("e-1", "f-1", "p-1");
    }

    [Fact]
    public void TruncationIsStillCounted()
    {
        var rows = Enumerable.Range(1, 8)
            .Select(i => R($"f-{i}", MemoryHistoryKind.Fact, $"fact {i}")).ToArray();

        var block = MemoryBlockRenderer.Render(rows, At, "alice", maxLines: 3);

        block.Lines.Should().HaveCount(3);
        block.OmittedCount.Should().Be(5);
    }

    [Fact]
    public void AnOwnerWithNoMemoriesRendersAnHonestEmptyBlock() =>
        MemoryBlockRenderer.ToText(MemoryBlockRenderer.Render([], At, "alice"))
            .Should().Contain("holds nothing");
}
