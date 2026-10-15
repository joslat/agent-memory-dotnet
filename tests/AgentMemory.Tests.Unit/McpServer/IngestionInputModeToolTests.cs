using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class IngestionInputModeToolTests
{
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();

    public IngestionInputModeToolTests()
    {
        _memory.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>()).Returns(new ExtractionResult());
    }

    [Fact]
    public void Schema_exposes_an_optional_named_mode_without_adding_another_tool()
    {
        using var provider = Provider();
        var tool = Tool(provider);
        var schema = tool.ProtocolTool.InputSchema;
        schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().NotContain("inputMode");
        var mode = schema.GetProperty("properties").GetProperty("inputMode");
        mode.GetProperty("type").GetString().Should().Be("string");
        mode.GetProperty("enum").EnumerateArray().Select(x => x.GetString())
            .Should().BeEquivalentTo("Unspecified", "ConversationTurn", "Import");
        mode.GetProperty("default").GetString().Should().Be("Unspecified");
    }

    [Theory]
    [InlineData(null, ExtractionInputMode.Unspecified)]
    [InlineData("Unspecified", ExtractionInputMode.Unspecified)]
    [InlineData("ConversationTurn", ExtractionInputMode.ConversationTurn)]
    [InlineData("Import", ExtractionInputMode.Import)]
    public async Task Protocol_arguments_preserve_mode_owner_and_message(string? mode, ExtractionInputMode expected)
    {
        using var provider = Provider();
        using var stop = new CancellationTokenSource();
        var result = await InvokeAsync(provider, mode, "user", stop.Token);
        result.IsError.Should().NotBe(true);
        await _memory.Received(1).ExtractAndPersistAsync(Arg.Is<ExtractionRequest>(r => r.InputMode == expected
            && r.SessionId == "session" && r.UserId == "owner" && r.Messages.Single().Content == "My sister lives in Madrid."), stop.Token);
    }

    [Theory]
    [InlineData("unknown", "user")]
    [InlineData("99", "user")]
    [InlineData("ConversationTurn", "assistant")]
    public async Task Invalid_mode_or_non_user_live_turn_never_reaches_memory(string mode, string role)
    {
        using var provider = Provider();
        // The SDK may reject argument decoding or represent a tool exception as IsError; neither may
        // dispatch an invalid request to the memory service, including custom service implementations.
        CallToolResult? result = null;
        Exception? error = null;
        try { result = await InvokeAsync(provider, mode, role, default); }
        catch (Exception ex) { error = ex; }
        (error is not null || result?.IsError == true).Should().BeTrue();
        await _memory.DidNotReceiveWithAnyArgs().ExtractAndPersistAsync(default!, default);
    }

    [Fact]
    public void Read_only_registration_still_excludes_the_write_tool()
    {
        using var provider = Provider(readOnly: true);
        provider.GetServices<McpServerTool>().Should().NotContain(t => t.ProtocolTool.Name == "extract_and_persist");
    }

    private ServiceProvider Provider(bool readOnly = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_memory);
        var ids = Substitute.For<IIdGenerator>();
        ids.GenerateId().Returns("message");
        services.AddSingleton(ids);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        services.AddSingleton(clock);
        services.AddMcpServer().AddAgentMemoryMcpTools(o => o.ReadOnly = readOnly);
        return services.BuildServiceProvider();
    }

    private static McpServerTool Tool(ServiceProvider provider) =>
        provider.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == "extract_and_persist");

    private static async Task<CallToolResult> InvokeAsync(ServiceProvider provider, string? mode, string role, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["messageText"] = JsonSerializer.SerializeToElement("My sister lives in Madrid."),
            ["sessionId"] = JsonSerializer.SerializeToElement("session"),
            ["userId"] = JsonSerializer.SerializeToElement("owner"),
            ["role"] = JsonSerializer.SerializeToElement(role),
        };
        if (mode is not null) args["inputMode"] = JsonSerializer.SerializeToElement(mode);
        var server = Substitute.For<ModelContextProtocol.Server.McpServer>();
        var context = new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" },
            new CallToolRequestParams { Name = "extract_and_persist", Arguments = args }) { Services = provider };
        return await Tool(provider).InvokeAsync(context, cancellationToken);
    }
}
