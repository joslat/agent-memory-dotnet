using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.AgentFramework.Nams.Mapping;
using AgentMemory.Nams.Recall;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class NamsMafTypeMapperTests
{
    [Theory]
    [InlineData(NamsRecallProvenance.Untrusted, MemoryTrustLevel.Untrusted)]
    [InlineData(NamsRecallProvenance.UserProvided, MemoryTrustLevel.UserProvided)]
    [InlineData(NamsRecallProvenance.ModelGenerated, MemoryTrustLevel.ModelGenerated)]
    [InlineData(NamsRecallProvenance.ToolDerived, MemoryTrustLevel.ToolDerived)]
    [InlineData(NamsRecallProvenance.VerifiedExternal, MemoryTrustLevel.VerifiedExternal)]
    [InlineData(NamsRecallProvenance.ApplicationTrusted, MemoryTrustLevel.ApplicationTrusted)]
    public void ToTrustLevel_MapsOneToOne(NamsRecallProvenance provenance, MemoryTrustLevel expected)
    {
        NamsMafTypeMapper.ToTrustLevel(provenance).Should().Be(expected);
    }
}
