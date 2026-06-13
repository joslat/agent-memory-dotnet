using System.Text.Json;
using FluentAssertions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class MaintenanceToolsTests
{
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();

    // ── memory_invalidate ──

    [Fact]
    public async Task MemoryInvalidate_Fact_Scoped_RoutesToFact_AndOwnerScopes()
    {
        _longTerm.InvalidateFactAsync("f1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var json = await MaintenanceTools.MemoryInvalidate(_longTerm, "fact", "f1", userId: "alice");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().Should().Be("fact");
        doc.RootElement.GetProperty("invalidated").GetBoolean().Should().BeTrue();
        await _longTerm.Received(1).InvalidateFactAsync(
            "f1", Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryInvalidate_Entity_BlankUserId_PassesNullScope()
    {
        _longTerm.InvalidateEntityAsync("e1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(false);

        var json = await MaintenanceTools.MemoryInvalidate(_longTerm, "entity", "e1", userId: "");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("invalidated").GetBoolean().Should().BeFalse();
        await _longTerm.Received(1).InvalidateEntityAsync("e1", Arg.Is<MemoryScope?>(s => s == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryInvalidate_Preference_Routes()
    {
        _longTerm.InvalidatePreferenceAsync("p1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await MaintenanceTools.MemoryInvalidate(_longTerm, "preference", "p1");

        await _longTerm.Received(1).InvalidatePreferenceAsync("p1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryInvalidate_UnknownType_ReturnsError_WithoutCallingService()
    {
        var json = await MaintenanceTools.MemoryInvalidate(_longTerm, "widget", "x");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        await _longTerm.DidNotReceive().InvalidateFactAsync(Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    // ── memory_supersede ──

    [Fact]
    public async Task MemorySupersede_Fact_Scoped_RoutesAndOwnerScopes()
    {
        _longTerm.SupersedeFactAsync("loser", "winner", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var json = await MaintenanceTools.MemorySupersede(_longTerm, "fact", "loser", "winner", userId: "alice");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("superseded").GetBoolean().Should().BeTrue();
        await _longTerm.Received(1).SupersedeFactAsync(
            "loser", "winner", Arg.Is<MemoryScope?>(s => s != null && s.OwnerId == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySupersede_Preference_Routes()
    {
        _longTerm.SupersedePreferenceAsync("l", "w", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await MaintenanceTools.MemorySupersede(_longTerm, "preference", "l", "w");

        await _longTerm.Received(1).SupersedePreferenceAsync("l", "w", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySupersede_UnknownType_ReturnsError_WithoutCallingService()
    {
        var json = await MaintenanceTools.MemorySupersede(_longTerm, "entity", "l", "w");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out _).Should().BeTrue();
        await _longTerm.DidNotReceive().SupersedeFactAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }
}
