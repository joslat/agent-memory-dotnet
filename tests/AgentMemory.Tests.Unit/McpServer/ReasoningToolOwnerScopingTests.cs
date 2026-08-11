using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// An MCP-started reasoning trace must carry the caller's owner, like every other tenant-facing tool.
/// </summary>
/// <remarks>
/// <c>memory_start_trace</c> passed no owner at all, so the trace went to the shared/global bucket —
/// while <c>MemoryQueryFacade</c> READS traces through the ambient owner context. The result was an
/// isolation asymmetry in which a trace written by one tenant could be invisible to that same tenant
/// on recall, and visible to every other one.
/// </remarks>
public sealed class ReasoningToolOwnerScopingTests
{
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();

    private static readonly IOptions<AgentMemoryMcpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new AgentMemoryMcpOptions());

    public ReasoningToolOwnerScopingTests()
    {
        _reasoning.StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<float[]?>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReasoningTrace
            {
                TraceId = "t1", SessionId = "s1", Task = "task", StartedAtUtc = DateTimeOffset.UnixEpoch,
            }));
    }

    [Fact]
    public async Task TheCallersUserIdBecomesTheTraceOwner()
    {
        await ReasoningTools.MemoryStartTrace(
            _reasoning, Options, task: "solve it", sessionId: "s1", userId: "tenant-a");

        await _reasoning.Received(1).StartTraceAsync(
            "s1", "solve it", Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            "tenant-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOmittedUserIdStillMeansSharedRatherThanThrowing()
    {
        // Single-tenant hosts pass no user id and must keep working: null flows to the isolation
        // policy, which decides. The tool must not invent an owner of its own.
        await ReasoningTools.MemoryStartTrace(
            _reasoning, Options, task: "solve it", sessionId: "s1");

        await _reasoning.Received(1).StartTraceAsync(
            "s1", "solve it", Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            null, Arg.Any<CancellationToken>());
    }
}
