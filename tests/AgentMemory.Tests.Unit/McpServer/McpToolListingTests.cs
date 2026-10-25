using System.Reflection;
using System.Text.Json;
using AgentMemory.McpServer;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// A server lists only what it will do: no graph tool that refuses every call, no extraction tool that stores
/// nothing, and on a server pinned to one owner no tool that cannot be confined to that owner.
/// </summary>
public sealed class McpToolListingTests
{
    private static IReadOnlyList<McpServerTool> RegisteredTools(Action<AgentMemoryMcpOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddMcpServer().AddAgentMemoryMcpTools(configure);
        using var empty = new ServiceCollection().BuildServiceProvider();
        return services
            .Where(descriptor => descriptor.ServiceType == typeof(McpServerTool))
            .Select(descriptor => descriptor.ImplementationFactory?.Invoke(empty) as McpServerTool)
            .Where(tool => tool is not null)
            .Select(tool => tool!)
            .ToList();
    }

    private static IReadOnlyList<string> Listed(Action<AgentMemoryMcpOptions> configure) =>
        RegisteredTools(configure).Select(tool => tool.ProtocolTool.Name).ToList();

    private static IReadOnlySet<string> DeclaredToolNames() => typeof(AgentMemoryMcpOptions).Assembly.GetTypes()
        .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .Where(name => !string.IsNullOrEmpty(name)).Select(name => name!)
        .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void TheNewToolSetsNameOnlyDeclaredTools()
    {
        var declared = DeclaredToolNames();
        McpToolAccess.GraphTools.Should().OnlyContain(name => declared.Contains(name)).And.HaveCount(3);
        McpToolAccess.ExtractionTools.Should().OnlyContain(name => declared.Contains(name)).And.HaveCount(2);
    }

    [Fact]
    public void GraphToolsAreNotListedUnlessGraphQueryIsEnabled()
    {
        // They used to be listed always and refuse every call: a listed tool is one a model will try.
        Listed(_ => { }).Should().NotContain(McpToolAccess.GraphTools);
        Listed(o => o.EnableGraphQuery = true).Should().Contain(McpToolAccess.GraphTools);
    }

    [Fact]
    public void ExtractionToolsAreNotListedWithoutAnExtractionModel()
    {
        Listed(o => o.EnableExtraction = false).Should().NotContain(McpToolAccess.ExtractionTools);
        Listed(_ => { }).Should().Contain(McpToolAccess.ExtractionTools, "extraction stays on by default for existing hosts");
    }

    [Fact]
    public void APinnedServerNeverListsTheGraphToolsEvenWhenEnabled()
    {
        var listed = Listed(o => { o.EnableGraphQuery = true; o.OwnerId = "alice"; });
        listed.Should().NotContain(McpToolAccess.GraphTools, "none of them can be confined to one owner");
        listed.Should().Contain("memory_search");
    }

    [Fact]
    public void WithGraphQueryEnabledAndNothingElseSetEveryDeclaredToolIsListed()
    {
        Listed(o => o.EnableGraphQuery = true).Should().BeEquivalentTo(DeclaredToolNames());
    }

    // ── the owner pin ───────────────────────────────────────────────────

    private static Dictionary<string, JsonElement> Arguments(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("""{"query":"q","userId":"bob"}""", true)]
    [InlineData("""{"query":"q","userId":"alice"}""", false)]
    [InlineData("""{"query":"q","userId":null}""", false)]
    [InlineData("""{"query":"q","userId":""}""", false)]
    [InlineData("""{"query":"q"}""", false)]
    [InlineData("""{"query":"q","userId":7}""", true)]
    public void OnlyAnotherNamedOwnerIsRefused(string json, bool refused) =>
        McpOwnerPin.NamesAnotherOwner("alice", Arguments(json)).Should().Be(refused);

    [Fact]
    public void NoArgumentsNameNoOwner() => McpOwnerPin.NamesAnotherOwner("alice", null).Should().BeFalse();

    [Fact]
    public void ThePinnedOwnerIsFilledInWithoutChangingTheCallersArguments()
    {
        var original = Arguments("""{"query":"q","userId":null}""");
        var pinned = McpOwnerPin.WithOwner("al\"ice", original);
        pinned["userId"].GetString().Should().Be("al\"ice");
        pinned["query"].GetString().Should().Be("q");
        original["userId"].ValueKind.Should().Be(JsonValueKind.Null);
        McpOwnerPin.WithOwner("alice", null)["userId"].GetString().Should().Be("alice");
    }

    [Fact]
    public void OnlyToolsThatDeclareUserIdTakeTheOwner()
    {
        var tools = RegisteredTools(o => o.EnableGraphQuery = true).ToDictionary(t => t.ProtocolTool.Name);
        McpOwnerPin.AcceptsOwner(tools["memory_search"].ProtocolTool.InputSchema).Should().BeTrue();
        McpOwnerPin.AcceptsOwner(tools["memory_add_fact"].ProtocolTool.InputSchema).Should().BeTrue();
        McpOwnerPin.AcceptsOwner(tools["memory_record_step"].ProtocolTool.InputSchema).Should().BeFalse();
        McpOwnerPin.AcceptsOwner(tools["memory_store_message"].ProtocolTool.InputSchema).Should().BeFalse();
        McpOwnerPin.AcceptsOwner(null).Should().BeFalse();
    }

    [Fact]
    public void AListedCopyLosesUserIdAndTheRegisteredToolKeepsIt()
    {
        var tool = RegisteredTools(_ => { }).Single(t => t.ProtocolTool.Name == "memory_search").ProtocolTool;
        var copy = McpOwnerPin.WithoutOwner(tool);
        McpOwnerPin.AcceptsOwner(copy.InputSchema).Should().BeFalse();
        copy.InputSchema.GetProperty("properties").TryGetProperty("query", out _).Should().BeTrue();
        copy.Name.Should().Be(tool.Name);
        copy.Description.Should().Be(tool.Description);
        McpOwnerPin.AcceptsOwner(tool.InputSchema).Should().BeTrue("the call filter reads the registered schema");
    }

    [Fact]
    public void ARequiredUserIdIsNoLongerRequiredInTheCopy()
    {
        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"userId":{"type":"string"},"q":{"type":"string"}},"required":["q","userId"]}""");
        var copy = McpOwnerPin.WithoutOwner(new Tool { Name = "t", InputSchema = schema.RootElement.Clone() });
        copy.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("q");
    }

    [Fact]
    public void ResourcesReadThePinnedOwnerAndRefuseAnother()
    {
        var pinned = new AgentMemoryMcpOptions { OwnerId = "alice" };
        McpOwnerPin.ForResource(pinned, null).Should().Be("alice");
        McpOwnerPin.ForResource(pinned, "alice").Should().Be("alice");
        FluentActions.Invoking(() => McpOwnerPin.ForResource(pinned, "bob")).Should().Throw<McpException>();
        McpOwnerPin.ForResource(new AgentMemoryMcpOptions(), "bob").Should().Be("bob", "nothing pinned keeps the caller's");
        McpOwnerPin.ForResource(new AgentMemoryMcpOptions { OwnerId = "  " }, null).Should().BeNull("blank pins nothing");
    }

    [Fact]
    public void ThePinIsOffByDefault() => new AgentMemoryMcpOptions().OwnerId.Should().BeNull();
}
