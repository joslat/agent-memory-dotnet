using AgentMemory.Abstractions.Options;
using AgentMemory.RouterArena.Record;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.RouterArena;

public sealed class BenchmarkMemoryOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_history_choice_overrides_recommended_projection_without_changing_other_options(bool history)
    {
        var actual = BenchmarkMemoryOptions.CreateRecommended(history);
        actual.Projection.ResolveSupersessions.Should().Be(history);
#pragma warning disable AMREC001
        var recommended = MemoryOptions.CreateRecommended();
#pragma warning restore AMREC001
        (actual with { Projection = actual.Projection with { ResolveSupersessions = recommended.Projection.ResolveSupersessions } })
            .Should().BeEquivalentTo(recommended);
    }
}
