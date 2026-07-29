using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

public sealed class LongMemEvalPreparedMemoryTests
{
    [Fact]
    public void PreparedEvaluation_RequiresAnExplicitSealedStateAuthority()
    {
        var preparedState = typeof(LongMemEvalAdapterOptions)
            .GetProperty("PreparedState");

        preparedState.Should().NotBeNull(
            "skipping storage and extraction is safe only after a sealed manifest validates the exact prepared state");
    }
}
