using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.McpHost;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

#pragma warning disable AMGATE001

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class McpHostRetrievalTests
{
    [Theory]
    [InlineData(null, MemoryGateMode.Judge)]
    [InlineData("Floor", MemoryGateMode.Floor)]
    public void Explicit_endpoint_configuration_installs_the_gate_and_respects_the_mode(string? mode, MemoryGateMode expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["AgentMemory:RetrievalRouter:Judges:0:Name"] = "local",
            ["AgentMemory:RetrievalRouter:Judges:0:Endpoint"] = "http://127.0.0.1:8765/v1/systemone",
            ["AgentMemory:RetrievalRouter:Judges:0:Weight"] = "0.2",
        };
        if (mode is not null) values["AgentMemory:RetrievalRouter:Mode"] = mode;
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMemoryContextAssembler>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        McpHostProgram.ConfigureRetrieval(services, configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMemoryContextAssembler>().Should().BeOfType<GatedMemoryContextAssembler>();
        var gate = provider.GetRequiredService<IOptions<MemoryGateOptions>>().Value;
        gate.Mode.Should().Be(expected);
        gate.Judges.Should().ContainSingle().Which.Name.Should().Be("local");
        gate.Judges[0].Weight.Should().Be(0.2);
        gate.UpdateJudge.Should().BeFalse("general MCP retrieval configuration does not install a conversation writer");
    }

    [Fact]
    public void Ambient_credentials_alone_leave_the_original_assembler_unchanged()
    {
        var services = new ServiceCollection();
        var original = Substitute.For<IMemoryContextAssembler>();
        services.AddSingleton(original);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TYPESAFE_API_KEY"] = "synthetic-unused-key",
        }).Build();

        McpHostProgram.ConfigureRetrieval(services, configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMemoryContextAssembler>().Should().BeSameAs(original);
        provider.GetService<IMemoryGate>().Should().BeNull();
    }
}
