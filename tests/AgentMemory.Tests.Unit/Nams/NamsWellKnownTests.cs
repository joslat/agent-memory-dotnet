using FluentAssertions;
using AgentMemory.Nams;

namespace AgentMemory.Tests.Unit.Nams;

public sealed class NamsWellKnownTests
{
    [Fact]
    public void Endpoint_IsThePublicNamsSaasBaseUrl()
    {
        NamsWellKnown.Endpoint.Should().Be(new Uri("https://memory.neo4jlabs.com/v1"));
    }
}
