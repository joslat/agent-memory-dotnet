using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.McpServer.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class GraphQueryToolsTests
{
    private readonly IGraphQueryService _graphQueryService = Substitute.For<IGraphQueryService>();

    // ── graph_query disabled ──

    [Fact]
    public async Task GraphQuery_ThrowsMcpExceptionWhenEnableGraphQueryIsFalse()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = false });

        var act = () => GraphQueryTools.GraphQuery(_graphQueryService, options, "MATCH (n) RETURN n");

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task GraphQuery_ThrowsMcpExceptionWithDescriptiveMessage()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = false });

        var act = () => GraphQueryTools.GraphQuery(_graphQueryService, options, "MATCH (n) RETURN n");

        await act.Should().ThrowAsync<McpException>()
            .WithMessage("*graph_query*disabled*");
    }

    // ── graph_query enabled ──

    [Fact]
    public async Task GraphQuery_CallsQueryAsyncWhenEnabled()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        await GraphQueryTools.GraphQuery(_graphQueryService, options, "MATCH (n) RETURN n");

        await _graphQueryService.Received(1).QueryAsync(
            "MATCH (n) RETURN n",
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GraphQuery_ReturnsJsonWithRowCountAndRows()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["name"] = "Alice" },
            new Dictionary<string, object?> { ["name"] = "Bob" }
        };
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await GraphQueryTools.GraphQuery(_graphQueryService, options, "MATCH (n) RETURN n.name");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("rowCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("rows").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GraphQuery_ReturnsEmptyResultsCorrectly()
    {
        var options = Options.Create(new McpServerOptions { EnableGraphQuery = true });
        _graphQueryService.QueryAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        var result = await GraphQueryTools.GraphQuery(_graphQueryService, options, "MATCH (n:NonExistent) RETURN n");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("rowCount").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("rows").GetArrayLength().Should().Be(0);
    }
}
