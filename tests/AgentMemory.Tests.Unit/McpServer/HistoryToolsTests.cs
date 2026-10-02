using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>PLAN 40.14 (F7): the MCP server reads time as well as writing it.</summary>
public sealed class HistoryToolsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IMemoryHistoryService _history = Substitute.For<IMemoryHistoryService>();
    private readonly IMemoryIsolationPolicy _policy = Substitute.For<IMemoryIsolationPolicy>();

    public HistoryToolsTests()
    {
        _memory.RecallAsOfAsync(Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new RecallResult { Context = new MemoryContext { SessionId = "s", AssembledAtUtc = T0 } });
        _policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(ci => ci.ArgAt<string?>(1) is { Length: > 0 } owner ? MemoryScope.For(owner) : MemoryScope.Global);
    }

    // ── memory_recall_as_of ──

    [Fact]
    public async Task RecallAsOf_PassesBothClocks_AndTheConfiguredRecallOptions()
    {
        // A host-configured value the tool must carry through (a tool that started from RecallOptions.Default would
        // drop it, the bug 25.2 fixed for memory_search), beside the per-call cap.
        var configured = new MemoryOptions { Recall = RecallOptions.Default with { MinSimilarityScore = 0.42 } };
        var json = await HistoryTools.MemoryRecallAsOf(_memory, Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(configured), "where do I live?", "2026-03-15", systemAsOf: "2026-04-01T09:00:00Z",
            userId: "alice", maxResults: 3);

        await _memory.Received(1).RecallAsOfAsync(
            Arg.Is<RecallRequest>(r => r.UserId == "alice" && r.Query == "where do I live?" && r.Options!.MaxFacts == 3
                && r.Options.MinSimilarityScore == 0.42),
            new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero),
            Arg.Any<CancellationToken>());
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("context", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RecallAsOf_WithoutSystemAsOf_LeavesIt_ToTheOneClock()
    {
        await HistoryTools.MemoryRecallAsOf(_memory, Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(new MemoryOptions()), "q", "2026-03-15T10:30:00+02:00");

        await _memory.Received(1).RecallAsOfAsync(Arg.Any<RecallRequest>(),
            new DateTimeOffset(2026, 3, 15, 8, 30, 0, TimeSpan.Zero), null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("last March", null)]
    [InlineData("2026-03-15", "yesterday")]
    public async Task RecallAsOf_AnUnreadableDate_IsAnError_AndRecallsNothing(string asOf, string? systemAsOf)
    {
        var json = await HistoryTools.MemoryRecallAsOf(_memory, Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(new MemoryOptions()), "q", asOf, systemAsOf);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("ISO-8601");
        await _memory.DidNotReceiveWithAnyArgs().RecallAsOfAsync(default!, default, default, default);
    }

    // ── memory_lineage ──

    private static MemoryHistoryRecord Fact(string id, int day, string[] supersedes, string[] supersededBy) => new()
    {
        Kind = MemoryHistoryKind.Fact,
        Id = id,
        Summary = $"user | lives in | {id}",
        Status = supersededBy.Length == 0 ? MemoryHistoryStatus.Live : MemoryHistoryStatus.Invalidated,
        CreatedAtUtc = T0.AddDays(day),
        InvalidatedAtUtc = supersededBy.Length == 0 ? null : T0.AddDays(day + 1),
        SupersedesIds = supersedes,
        SupersededByIds = supersededBy,
        SourceMessageIds = [$"m-{id}"],
    };

    private void Store(params MemoryHistoryRecord[] records) =>
        _history.GetHistoryAsync(Arg.Any<MemoryHistoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => records.Where(r => r.Id == ci.Arg<MemoryHistoryQuery>().Id).ToArray());

    [Fact]
    public async Task Lineage_WalksBothWays_OldestFirst_AndNamesWhatIsCurrent()
    {
        Store(Fact("hamburg", 0, [], ["lyon"]), Fact("lyon", 10, ["hamburg"], ["copenhagen"]), Fact("copenhagen", 20, ["lyon"], []));

        var json = await HistoryTools.MemoryLineage(_history, _policy, "fact", "lyon", userId: "alice");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("chain").EnumerateArray().Select(e => e.GetProperty("id").GetString())
            .Should().Equal("hamburg", "lyon", "copenhagen");
        doc.RootElement.GetProperty("current").EnumerateArray().Select(e => e.GetString()).Should().Equal("copenhagen");
        doc.RootElement.GetProperty("chain")[0].GetProperty("sourceMessageIds")[0].GetString().Should().Be("m-hamburg");
        await _history.Received().GetHistoryAsync(
            Arg.Is<MemoryHistoryQuery>(q => q.OwnerId == "alice" && q.Kind == MemoryHistoryKind.Fact && q.IncludeInvalidated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lineage_StopsAtTheDepthAsked()
    {
        Store(Fact("a", 0, [], ["b"]), Fact("b", 1, ["a"], ["c"]), Fact("c", 2, ["b"], []));

        var json = await HistoryTools.MemoryLineage(_history, _policy, "fact", "a", maxDepth: 1);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("chain").EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Lineage_OfAnUnknownId_IsNotFound()
    {
        Store();

        var json = await HistoryTools.MemoryLineage(_history, _policy, "fact", "nope");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("found").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Lineage_OfAnEntity_IsRefused_WithoutReading()
    {
        var json = await HistoryTools.MemoryLineage(_history, _policy, "entity", "e1");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("fact or preference");
        await _history.DidNotReceiveWithAnyArgs().GetHistoryAsync(default!, default);
    }

    [Fact]
    public async Task Lineage_FailsClosed_UnderStrictIsolation_BeforeAnyRead()
    {
        _policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Throws(new MemoryOwnerScopeRequiredException("memory_lineage"));

        var act = () => HistoryTools.MemoryLineage(_history, _policy, "fact", "a");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
        await _history.DidNotReceiveWithAnyArgs().GetHistoryAsync(default!, default);
    }
}
