using AgentMemory.Abstractions.Domain;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// Unit tests for the retroactive memory MCP tools: memory_extract_session and memory_generate_embeddings.
/// </summary>
public sealed class RetroactiveToolsTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();

    public RetroactiveToolsTests()
    {
        _memoryService
            .ExtractFromSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _memoryService
            .ExtractFromConversationAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _memoryService
            .GenerateEmbeddingsBatchAsync(Arg.Any<MemoryNodeKind>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(42);
    }

    // ── memory_extract_session ──

    [Fact]
    public async Task MemoryExtractSession_CallsExtractFromSessionAsyncWithGivenSessionId()
    {
        var options = Options.Create(new AgentMemoryMcpOptions { DefaultSessionId = "default-sess" });

        await AdvancedMemoryTools.MemoryExtractSession(_memoryService, options, "my-session");

        await _memoryService.Received(1).ExtractFromSessionAsync("my-session", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryExtractSession_UsesDefaultSessionIdWhenNotProvided()
    {
        var options = Options.Create(new AgentMemoryMcpOptions { DefaultSessionId = "default-sess" });

        await AdvancedMemoryTools.MemoryExtractSession(_memoryService, options);

        await _memoryService.Received(1).ExtractFromSessionAsync("default-sess", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryExtractSession_ReturnsJsonWithSessionIdAndStatus()
    {
        var options = Options.Create(new AgentMemoryMcpOptions { DefaultSessionId = "sess-x" });

        var result = await AdvancedMemoryTools.MemoryExtractSession(_memoryService, options, "sess-x");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("sess-x");
        doc.RootElement.GetProperty("status").GetString().Should().Be("extraction_complete");
    }

    // ── memory_generate_embeddings ──

    [Fact]
    public async Task MemoryGenerateEmbeddings_CallsGenerateEmbeddingsBatchAsyncWithLabel()
    {
        await AdvancedMemoryTools.MemoryGenerateEmbeddings(_memoryService, "Entity");

        await _memoryService.Received(1)
            .GenerateEmbeddingsBatchAsync(MemoryNodeKind.Entity, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGenerateEmbeddings_PassesBatchSizeToService()
    {
        await AdvancedMemoryTools.MemoryGenerateEmbeddings(_memoryService, "Fact", batchSize: 50);

        await _memoryService.Received(1)
            .GenerateEmbeddingsBatchAsync(MemoryNodeKind.Fact, 50, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryGenerateEmbeddings_ReturnsJsonWithNodeLabelAndCount()
    {
        var result = await AdvancedMemoryTools.MemoryGenerateEmbeddings(_memoryService, "Preference");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("nodeLabel").GetString().Should().Be("Preference");
        doc.RootElement.GetProperty("nodesUpdated").GetInt32().Should().Be(42);
    }

    [Theory]
    [InlineData("1")]        // a numeric string parses to a defined enum value — must still be rejected
    [InlineData("999")]      // out-of-range
    [InlineData("Message")]  // unknown label
    public async Task MemoryGenerateEmbeddings_NonNamedLabel_ReturnsError_AndDoesNotCallService(string label)
    {
        var result = await AdvancedMemoryTools.MemoryGenerateEmbeddings(_memoryService, label);

        JsonDocument.Parse(result).RootElement.TryGetProperty("error", out _).Should().BeTrue();
        await _memoryService.DidNotReceive()
            .GenerateEmbeddingsBatchAsync(Arg.Any<MemoryNodeKind>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
