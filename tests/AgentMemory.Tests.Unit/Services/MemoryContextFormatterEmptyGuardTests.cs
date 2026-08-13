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
    public void ATracesOnlyRecallDoesNotEmitABareHeading()
    {
        // Red before 0.6: this returned "## Memory Context" and nothing else.
        var formatted = MemoryContextFormatter.FormatRecallResult(TracesOnly(3));

        formatted.Should().BeEmpty(
            "a heading with no body tells the model memory is empty, when the truth is that this "
            + "formatter has no section for what was retrieved");
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
