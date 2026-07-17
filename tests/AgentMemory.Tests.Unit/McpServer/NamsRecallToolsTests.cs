using System.Text.Json;
using FluentAssertions;
using AgentMemory.McpServer.Nams.Tools;
using AgentMemory.Nams.Recall;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsRecallToolsTests
{
    private readonly INamsRecallService _recallService = Substitute.For<INamsRecallService>();

    [Fact]
    public async Task NamsRecall_PassesConversationIdAndQueryTextThrough()
    {
        _recallService.RecallAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new NamsRecallResult());

        await NamsRecallTools.NamsRecall(_recallService, "conv-1", "hello");

        await _recallService.Received(1).RecallAsync("conv-1", "hello", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamsRecall_QueryTextOmitted_PassesNull()
    {
        _recallService.RecallAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new NamsRecallResult());

        await NamsRecallTools.NamsRecall(_recallService, "conv-1");

        await _recallService.Received(1).RecallAsync("conv-1", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamsRecall_ReturnsSerializedItemsIsPartialAndWarnings()
    {
        _recallService.RecallAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new NamsRecallResult
            {
                Items =
                [
                    new NamsRecalledItem
                    {
                        SourceId = "m1",
                        Category = NamsRecallCategory.RecentMessage,
                        Content = "hi there",
                        Provenance = NamsRecallProvenance.UserProvided,
                        Role = "user"
                    }
                ],
                IsPartial = true,
                Warnings = [new NamsRecallWarning("context", "degraded")]
            });

        var json = await NamsRecallTools.NamsRecall(_recallService, "conv-1", "hi");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("isPartial").GetBoolean().Should().BeTrue();
        var item = doc.RootElement.GetProperty("items")[0];
        item.GetProperty("sourceId").GetString().Should().Be("m1");
        item.GetProperty("category").GetString().Should().Be(nameof(NamsRecallCategory.RecentMessage));
        item.GetProperty("content").GetString().Should().Be("hi there");
        var warning = doc.RootElement.GetProperty("warnings")[0];
        warning.GetProperty("category").GetString().Should().Be("context");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NamsRecall_MissingConversationId_ReturnsErrorWithoutCallingRecallService(string? namsConversationId)
    {
        var json = await NamsRecallTools.NamsRecall(_recallService, namsConversationId!, "hi");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        await _recallService.DidNotReceiveWithAnyArgs().RecallAsync(default!, default, default);
    }

    [Fact]
    public async Task NamsRecall_NoUserIdOrWorkspaceIdParameterExists()
    {
        // Structural guard for the plan's own Phase 8 test-list item ("no userId or workspace argument
        // exposed to model"): the only identity-shaped input this tool accepts is an opaque, already-
        // resolved conversation ID.
        var method = typeof(NamsRecallTools).GetMethod(nameof(NamsRecallTools.NamsRecall));
        var parameterNames = method!.GetParameters().Select(p => p.Name).ToList();

        parameterNames.Should().NotContain(n => n!.Contains("userId", StringComparison.OrdinalIgnoreCase));
        parameterNames.Should().NotContain(n => n!.Contains("workspace", StringComparison.OrdinalIgnoreCase));
    }
}
