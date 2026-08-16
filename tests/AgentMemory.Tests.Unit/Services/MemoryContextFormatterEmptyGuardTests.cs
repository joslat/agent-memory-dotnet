using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The formatter's empty guard (0.6).
/// </summary>
/// <remarks>
/// <para>
/// <c>FormatRecallResult</c> early-returns only when <c>TotalItemsRetrieved == 0</c>, and
/// <c>MemoryService</c> counts <c>SimilarTraces</c> into that total — while no section in the
/// formatter renders traces. At stock settings (<c>RecallOptions.MaxTraces</c> defaults to 3) a
/// traces-only recall therefore produced the bare string <c>"## Memory Context"</c>.
/// </para>
/// <para>
/// A heading with no body is worse than nothing: it tells the model memory was consulted and is
/// empty, when in truth the formatter has no channel for what was retrieved. That is a wrong
/// statement made confidently, which is the failure this project keeps paying for.
/// </para>
/// </remarks>
public sealed class MemoryContextFormatterEmptyGuardTests
{
    private static RecallResult TracesOnly(int traceCount) => new()
    {
        Context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UtcNow,
            SimilarTraces = new MemoryContextSection<ReasoningTrace>
            {
                Items = Enumerable.Range(0, traceCount).Select(index => new ReasoningTrace
                {
                    TraceId = $"t{index}",
                    SessionId = "s1",
                    Task = $"task {index}",
                    StartedAtUtc = DateTimeOffset.UtcNow,
                }).ToList(),
            },
        },
        // Exactly what MemoryService reports: traces are counted into the total.
        TotalItemsRetrieved = traceCount,
    };

    [Fact]
    public void ATracesOnlyRecallNowRendersItsTraces()
    {
        // SUPERSEDED BY THE FIX, and updated rather than deleted so the history is legible.
        //
        // 0.6 made this return empty, because the formatter had no trace section and a bare
        // "## Memory Context" told the model memory was empty when the truth was that the formatter
        // could not express what had been retrieved. 15.5 gave it a section, so the honest output is
        // no longer emptiness -- it is the traces.
        //
        // The guard itself still matters and is held by the tests below; what changed is that this
        // particular input is no longer an example of it.
        var formatted = MemoryContextFormatter.FormatRecallResult(TracesOnly(3));

        formatted.Should().Contain("## Memory Context");
        formatted.Should().Contain("task 0", "the traces are the body this heading was missing");
    }

    [Fact]
    public void AContextWhoseOnlyItemsAreUnrenderableStillReturnsEmpty()
    {
        // THE guard, restated on an input the formatter genuinely cannot express. GraphRagContext is
        // counted into TotalItemsRetrieved by callers but renders only when non-empty, so a context
        // reporting items with nothing renderable must say nothing rather than announce an empty
        // section.
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
            },
            // Deliberately inconsistent with the (empty) context: this is exactly the state that
            // produced a bare heading, arrived at by a caller's count rather than by content.
            TotalItemsRetrieved = 5,
        };

        MemoryContextFormatter.FormatRecallResult(result).Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyRecallStillReturnsEmpty()
    {
        // The pre-existing zero-items path must be untouched -- the guard collapses two states into
        // the one already handled, rather than introducing a third.
        var formatted = MemoryContextFormatter.FormatRecallResult(TracesOnly(0));

        formatted.Should().BeEmpty();
    }

    [Fact]
    public void ARecallWithRenderableContentIsUnaffected()
    {
        // The guard must not swallow real output. Asserted on content the formatter DOES render, so a
        // future over-eager empty check fails here rather than silently blanking every context.
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
                RelevantFacts = new MemoryContextSection<Fact>
                {
                    Items =
                    [
                        new Fact
                        {
                            FactId = "f1", Subject = "jose", Predicate = "lives in", Object = "Zurich",
                            Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow,
                        },
                    ],
                },
            },
            TotalItemsRetrieved = 1,
        };

        var formatted = MemoryContextFormatter.FormatRecallResult(result);

        formatted.Should().Contain("## Memory Context");
        formatted.Should().Contain("jose lives in Zurich");
    }
}
