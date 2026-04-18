using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.SemanticKernel;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.SemanticKernel;

public sealed class KernelMemoryExtensionsTests
{
    [Fact]
    public void AddNeo4jMemoryPlugin_KernelBuilder_RegistersPlugin()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IMemoryService>());
        builder.AddNeo4jMemoryPlugin();
        var kernel = builder.Build();
        kernel.Plugins.TryGetPlugin("Neo4jMemory", out var plugin).Should().BeTrue();
        plugin!.TryGetFunction("recall", out _).Should().BeTrue();
        plugin!.TryGetFunction("add_message", out _).Should().BeTrue();
    }

    [Fact]
    public void AddNeo4jMemoryPlugin_KernelBuilder_ReturnsBuilderForChaining()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IMemoryService>());
        builder.AddNeo4jMemoryPlugin().Should().BeSameAs(builder);
    }

    [Fact]
    public void AddNeo4jMemoryPlugin_Kernel_AddsPluginDirectly()
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.AddNeo4jMemoryPlugin(Substitute.For<IMemoryService>());
        kernel.Plugins.TryGetPlugin("Neo4jMemory", out var plugin).Should().BeTrue();
        plugin!.TryGetFunction("recall", out _).Should().BeTrue();
    }

    [Fact]
    public void AddNeo4jMemoryPlugin_Kernel_ReturnsKernelForChaining()
    {
        var kernel = Kernel.CreateBuilder().Build();
        kernel.AddNeo4jMemoryPlugin(Substitute.For<IMemoryService>()).Should().BeSameAs(kernel);
    }

    [Fact]
    public void AddNeo4jTextSearch_RegistersNeo4jTextSearch()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IMemoryService>());
        builder.AddNeo4jTextSearch("session-1");
        var kernel = builder.Build();
        kernel.Services.GetService<Neo4jTextSearch>().Should().NotBeNull();
    }

    [Fact]
    public void AddNeo4jTextSearch_ReturnsBuilderForChaining()
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IMemoryService>());
        builder.AddNeo4jTextSearch("s1").Should().BeSameAs(builder);
    }
}
