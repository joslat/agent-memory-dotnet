using FluentAssertions;
using AgentMemory.Nams.Observability;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsMetricsTests
{
    [Fact]
    public void MeterName_IsAgentMemoryNams()
    {
        NamsMetrics.MeterName.Should().Be("AgentMemory.Nams");
    }

    [Fact]
    public void Construction_DoesNotThrow()
    {
        var act = () => new NamsMetrics();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_DoesNotThrow_AndIsIdempotent()
    {
        var metrics = new NamsMetrics();

        var act = () => { metrics.Dispose(); metrics.Dispose(); };

        act.Should().NotThrow();
    }
}
