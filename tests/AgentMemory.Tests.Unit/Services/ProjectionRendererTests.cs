using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services.Projection;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 11. The one place a projection decision becomes text.
/// </summary>
/// <remarks>
/// This type exists because three surfaces used to decide rendering independently and drift — a
/// procedure-trust clause was once fixed in the benchmark harness while the product kept shipping the
/// contradiction. Its contract is therefore worth pinning precisely, not just exercising.
/// </remarks>
public sealed class ProjectionRendererTests
{
    private static ProjectedContext Context(
        Dictionary<string, ProjectedItemAnnotation>? annotations = null,
        List<ProjectedBlock>? blocks = null,
        Dictionary<string, IReadOnlyList<string>>? order = null) => new()
    {
        Annotations = annotations ?? new Dictionary<string, ProjectedItemAnnotation>(StringComparer.Ordinal),
        Blocks = blocks ?? [],
        SectionOrder = order ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
    };

    // ── identity when off ─────────────────────────────────────────────

    [Fact]
    public void ANullProjectionLeavesTheLineExactlyAsItWas()
    {
        ProjectionRenderer.AnnotateLine("- Bob works_at Acme", "f1", null)
            .Should().Be("- Bob works_at Acme");
    }

    [Fact]
    public void AnUnannotatedItemLeavesTheLineExactlyAsItWas()
    {
        ProjectionRenderer.AnnotateLine("- Bob works_at Acme", "f-absent", Context())
            .Should().Be("- Bob works_at Acme");
    }

    [Fact]
    public void ANullProjectionHasNoPreambleAndReordersNothing()
    {
        ProjectionRenderer.SectionPreamble("facts", null).Should().BeNull();

        var items = new[] { "a", "b" };
        ProjectionRenderer.Reorder("facts", items, x => x, null).Should().BeSameAs(items);
    }

    // ── annotation ────────────────────────────────────────────────────

    [Fact]
    public void ANearMissMarkerLeadsTheLineAndKeepsTheBullet()
    {
        // The caveat must be met before the claim, and a list must stay a list.
        var projection = Context(new()
        {
            ["f1"] = new ProjectedItemAnnotation { Score = 0.72, IsNearMiss = true },
        });

        ProjectionRenderer.AnnotateLine("- Bob works_at Acme", "f1", projection)
            .Should().Be("- [closest match, 0.72] Bob works_at Acme");
    }

    [Fact]
    public void ANearMissWithoutAScoreStillMarksTheLine()
    {
        // Score null means the provider could not rank it; the marker can still be honest without
        // inventing a number.
        var projection = Context(new()
        {
            ["f1"] = new ProjectedItemAnnotation { IsNearMiss = true },
        });

        ProjectionRenderer.AnnotateLine("- x", "f1", projection).Should().Be("- [closest match] x");
    }

    [Fact]
    public void AScoreAboveTheBarAddsNoMarker()
    {
        var projection = Context(new()
        {
            ["f1"] = new ProjectedItemAnnotation { Score = 0.99, IsNearMiss = false },
        });

        ProjectionRenderer.AnnotateLine("- x", "f1", projection).Should().Be("- x");
    }

    [Fact]
    public void EveryAnnotationRendersInAFixedOrder()
    {
        // Fixed so three surfaces produce the same string and a reader learns one shape.
        var projection = Context(new()
        {
            ["f1"] = new ProjectedItemAnnotation
            {
                Score = 0.5,
                IsNearMiss = true,
                SourceDate = "2023-05-12",
                SupersessionNote = "(since 2023-05-12; previously Globex)",
                SourceQuote = "I joined Acme last spring",
            },
        });

        ProjectionRenderer.AnnotateLine("- Bob works_at Acme", "f1", projection)
            .Should().Be(
                "- [closest match, 0.50] Bob works_at Acme (2023-05-12) "
                + "(since 2023-05-12; previously Globex) — said: \"I joined Acme last spring\"");
    }

    [Fact]
    public void ALineWithoutABulletIsAnnotatedInPlace()
    {
        // The benchmark surface renders "[fact] ..." rather than "- ...".
        var projection = Context(new()
        {
            ["f1"] = new ProjectedItemAnnotation { IsNearMiss = true, Score = 0.4 },
        });

        ProjectionRenderer.AnnotateLine("[fact] Bob works_at Acme", "f1", projection)
            .Should().Be("[closest match, 0.40] [fact] Bob works_at Acme");
    }

    // ── section preamble ──────────────────────────────────────────────

    [Fact]
    public void OnlyBlocksForTheAskedSectionAreReturned()
    {
        var projection = Context(blocks:
        [
            new ProjectedBlock(ProjectedBlockKind.NoDirectMatch, "facts", "no fact matched"),
            new ProjectedBlock(ProjectedBlockKind.NoDirectMatch, "traces", "no trace matched"),
        ]);

        ProjectionRenderer.SectionPreamble("facts", projection).Should().Be("no fact matched");
    }

    [Fact]
    public void SeveralBlocksForOneSectionAreJoined()
    {
        // A section can carry both a no-direct-match line and one or more conflicts; every surface
        // would otherwise need its own loop to place them.
        var projection = Context(blocks:
        [
            new ProjectedBlock(ProjectedBlockKind.NoDirectMatch, "facts", "no fact matched"),
            new ProjectedBlock(ProjectedBlockKind.ConflictingMemory, "facts", "CONFLICTING MEMORY — a / b"),
        ]);

        ProjectionRenderer.SectionPreamble("facts", projection)
            .Should().Be("no fact matched\nCONFLICTING MEMORY — a / b");
    }

    [Fact]
    public void ASectionWithNoBlocksHasNoPreamble()
    {
        ProjectionRenderer.SectionPreamble("entities", Context(blocks:
        [
            new ProjectedBlock(ProjectedBlockKind.NoDirectMatch, "facts", "x"),
        ])).Should().BeNull();
    }

    // ── reordering ────────────────────────────────────────────────────

    [Fact]
    public void AnOrderedSectionIsReordered()
    {
        var projection = Context(order: new() { ["facts"] = new[] { "c", "a", "b" } });

        ProjectionRenderer.Reorder("facts", new[] { "a", "b", "c" }, x => x, projection)
            .Should().Equal("c", "a", "b");
    }

    [Fact]
    public void ItemsMissingFromTheOrderKeepTheirPlaceAtTheEndRatherThanVanishing()
    {
        // An ordering feature that could lose an item would be a retrieval bug wearing a rendering
        // costume.
        var projection = Context(order: new() { ["facts"] = new[] { "b" } });

        ProjectionRenderer.Reorder("facts", new[] { "a", "b", "c" }, x => x, projection)
            .Should().BeEquivalentTo(["a", "b", "c"])
            .And.HaveCount(3);
    }

    [Fact]
    public void ASectionWithNoRecordedOrderIsUntouched()
    {
        var items = new[] { "a", "b" };
        var projection = Context(order: new() { ["traces"] = new[] { "b", "a" } });

        ProjectionRenderer.Reorder("facts", items, x => x, projection).Should().BeSameAs(items);
    }
}
