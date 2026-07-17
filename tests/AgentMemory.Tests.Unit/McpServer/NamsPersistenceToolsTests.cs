using System.Text.Json;
using FluentAssertions;
using AgentMemory.McpServer.Nams.Tools;
using AgentMemory.Nams.Persistence;
using NSubstitute;

namespace AgentMemory.Tests.Unit.McpServer;

public sealed class NamsPersistenceToolsTests
{
    private readonly INamsPersistenceService _persistenceService = Substitute.For<INamsPersistenceService>();

    [Fact]
    public async Task NamsRemember_UserRole_PersistsAsUserMessage()
    {
        _persistenceService
            .PersistTurnAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<CancellationToken>())
            .Returns(new NamsPersistenceResult { Outcome = NamsPersistenceOutcome.Persisted, PersistedMessageIds = ["m1"] });

        await NamsPersistenceTools.NamsRemember(_persistenceService, "conv-1", "hello", "user");

        await _persistenceService.Received(1).PersistTurnAsync(
            "conv-1",
            Arg.Is<IReadOnlyList<NamsMessageToPersist>>(l => l.Count == 1 && l[0].Content == "hello"),
            Arg.Is<IReadOnlyList<NamsMessageToPersist>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamsRemember_AssistantRole_PersistsAsAssistantMessage()
    {
        _persistenceService
            .PersistTurnAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<CancellationToken>())
            .Returns(new NamsPersistenceResult { Outcome = NamsPersistenceOutcome.Persisted });

        await NamsPersistenceTools.NamsRemember(_persistenceService, "conv-1", "hi back", "Assistant"); // case-insensitive

        await _persistenceService.Received(1).PersistTurnAsync(
            "conv-1",
            Arg.Is<IReadOnlyList<NamsMessageToPersist>>(l => l.Count == 0),
            Arg.Is<IReadOnlyList<NamsMessageToPersist>>(l => l.Count == 1 && l[0].Content == "hi back"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("system")]
    [InlineData("tool")]
    [InlineData("admin")]
    public async Task NamsRemember_DisallowedRole_RejectsWithoutCallingPersistenceService(string role)
    {
        var json = await NamsPersistenceTools.NamsRemember(_persistenceService, "conv-1", "hello", role);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("persisted").GetBoolean().Should().BeFalse();
        await _persistenceService.DidNotReceiveWithAnyArgs().PersistTurnAsync(
            default!, default!, default!, default);
    }

    [Fact]
    public async Task NamsRemember_ReturnsOutcomeAndMessageIds()
    {
        _persistenceService
            .PersistTurnAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<IReadOnlyList<NamsMessageToPersist>>(), Arg.Any<CancellationToken>())
            .Returns(new NamsPersistenceResult { Outcome = NamsPersistenceOutcome.Persisted, PersistedMessageIds = ["m1", "m2"] });

        var json = await NamsPersistenceTools.NamsRemember(_persistenceService, "conv-1", "hello", "user");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("persisted").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("outcome").GetString().Should().Be(nameof(NamsPersistenceOutcome.Persisted));
        doc.RootElement.GetProperty("messageIds").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("m1", "m2");
    }

    [Fact]
    public void NamsRemember_NoUserIdOrWorkspaceIdParameterExists()
    {
        var method = typeof(NamsPersistenceTools).GetMethod(nameof(NamsPersistenceTools.NamsRemember));
        var parameterNames = method!.GetParameters().Select(p => p.Name).ToList();

        parameterNames.Should().NotContain(n => n!.Contains("userId", StringComparison.OrdinalIgnoreCase));
        parameterNames.Should().NotContain(n => n!.Contains("workspace", StringComparison.OrdinalIgnoreCase));
    }
}
