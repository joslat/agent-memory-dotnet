using System.Reflection;
using System.Xml.Linq;
using System.Text.Json;
using System.ComponentModel;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace AgentMemory.Tests.Unit.Documentation;

/// <summary>
/// CI guard on the Copilot Studio connector definition (PLAN 12.8).
/// </summary>
/// <remarks>
/// <para>
/// The connector is <b>untested in anger</b> — it has never been imported into a live Copilot Studio
/// tenant, and that gate is outside this repository. What is <i>not</i> outside this repository is
/// whether the definition still describes the server it points at: the route, the protocol contract,
/// and the tool names it advertises. Those are exactly the parts that rot silently, because nothing
/// imports the file on the way past.
/// </para>
/// <para>
/// So this closes the half that can be closed. A connector that names a tool the server no longer
/// exposes is worse than one that names none: it reads as verified, and it fails on first contact —
/// which is precisely the hazard the connector's own README flags.
/// </para>
/// </remarks>
public sealed class CopilotStudioConnectorTests
{
    private static JsonDocument Definition() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "connectors", "copilot-studio", "apiDefinition.swagger.json")));

    /// <summary>
    /// Every tool name the MCP server actually exposes, read off the attributes rather than a list.
    /// </summary>
    /// <remarks>
    /// Reflected, not hand-maintained: a duplicated list of tool names is a second source of truth that
    /// drifts, which is the failure this test exists to catch in the connector.
    /// </remarks>
    private static IReadOnlySet<string> ServerToolNames()
    {
        var assembly = typeof(AgentMemory.McpServer.ServiceCollectionExtensions).Assembly;
        return assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheDefinitionIsAWellFormedSwagger2Document()
    {
        using var definition = Definition();

        definition.RootElement.GetProperty("swagger").GetString().Should().Be("2.0",
            "Copilot Studio custom connectors are imported as OpenAPI 2.0, not 3.x");
    }

    [Fact]
    public void TheMcpEndpointIsDeclaredAtTheRouteTheHostActuallyServes()
    {
        // The host calls app.MapMcp() with no prefix, which maps the streamable HTTP transport at the
        // ROOT. A connector pointing at /mcp or /api would import cleanly and fail at run time, and the
        // failure would look like a Copilot Studio problem rather than a path typo.
        using var definition = Definition();
        var paths = definition.RootElement.GetProperty("paths");

        paths.TryGetProperty("/", out var root).Should().BeTrue(
            "the MCP host maps the transport at the root route");
        root.TryGetProperty("post", out var post).Should().BeTrue(
            "streamable HTTP MCP is a POST endpoint");
        post.GetProperty("x-ms-agentic-protocol").GetString().Should().Be("mcp-streamable-1.0",
            "this extension is what tells Copilot Studio to treat the endpoint as MCP rather than REST");
    }

    [Fact]
    public void EveryToolTheConnectorAdvertisesStillExistsOnTheServer()
    {
        // THE drift test. The description sells a specific tool surface; if a tool is renamed, the
        // connector keeps advertising the old name and nothing here notices.
        using var definition = Definition();
        var description = definition.RootElement
            .GetProperty("paths").GetProperty("/").GetProperty("post")
            .GetProperty("description").GetString()!;

        var advertised = description
            .Split([' ', ',', '.', ':', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("memory_", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        advertised.Should().NotBeEmpty("the connector description names the tool surface it exposes");
        advertised.Should().BeSubsetOf(ServerToolNames(),
            "a connector naming a tool the server no longer exposes reads as verified and fails on first contact");
    }

    [Fact]
    public void TheUnauthenticatedHostHazardIsStatedInTheDefinitionItself()
    {
        // Not only in the README. The MCP host performs no authentication of its own, so the
        // Authorization header is forwarded and never validated -- exposing the host directly publishes
        // read AND write access to every memory it holds. Whoever imports this file must meet that
        // sentence without having to find the README first.
        using var definition = Definition();
        var security = definition.RootElement
            .GetProperty("securityDefinitions").GetProperty("api_key")
            .GetProperty("description").GetString()!;

        security.Should().Contain("NOT authenticated",
            "the hazard must travel with the artifact that gets imported");
    }

    [Fact]
    public void TheHostIsAPlaceholderRatherThanSomeonesRealDeployment()
    {
        // A committed hostname is how a sample ends up pointing at a machine that once existed.
        using var definition = Definition();

        definition.RootElement.GetProperty("host").GetString()
            .Should().Contain("REPLACE", "the definition must not carry a real deployment's hostname");
    }

    [Fact]
    public void TheReadmeInvokesTheToolNameThatIsActuallyShipped()
    {
        // 0.10. The README said `agentmemory-mcp`; the shipped ToolCommandName is `agent-memory-mcp`.
        // Step 1 of the only documented path hard-failed at the shell, before anything this connector
        // does could be reached -- and the connector's own README warns that a doc which looks right
        // and has never been run is exactly what gets announced and fails on first contact.
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "connectors", "copilot-studio", "README.md"));
        var toolName = ShippedToolCommandName();

        readme.Should().Contain(toolName);
        readme.Should().NotContain("agentmemory-mcp", "that binary has never existed");
    }

    [Fact]
    public void TheReadmeOnlyUsesFlagsTheHostAccepts()
    {
        // McpHostOptions treats an unknown flag as FATAL rather than ignoring it -- deliberately, so a
        // typo in --read-only cannot start a writable server. That makes every flag in the README a
        // hard dependency: `--http-url` and `--neo4j-uri` did not exist, so the documented command
        // aborted on startup.
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "connectors", "copilot-studio", "README.md"));
        var known = KnownHostFlags();

        // Only lines that INVOKE the host. `dotnet tool install --global` is a dotnet flag and has
        // nothing to do with McpHostOptions; checking it would make the guard fail on correct docs,
        // which is the fastest way to get a guard deleted.
        var used = readme
            .Split('\n')
            .Where(line => line.Contains(ShippedToolCommandName(), StringComparison.Ordinal))
            .SelectMany(line => System.Text.RegularExpressions.Regex
                .Matches(line, @"(?<![\w-])--[a-z][a-z0-9-]*")
                .Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        used.Should().NotBeEmpty();
        used.Should().BeSubsetOf(known,
            "every flag the README tells an operator to type must be one the host accepts");
    }

    /// <summary>The tool command name from the host's own csproj, never a duplicated literal.</summary>
    private static string ShippedToolCommandName()
    {
        var csproj = XDocument.Load(Path.Combine(
            RepoRoot(), "tools", "AgentMemory.McpHost", "AgentMemory.McpHost.csproj"));
        return csproj.Descendants("ToolCommandName").Single().Value;
    }

    /// <summary>The host's accepted flags, reflected off its own parser.</summary>
    private static IReadOnlyList<string> KnownHostFlags()
    {
        var type = typeof(AgentMemory.McpHost.McpHostOptions);
        var field = type.GetField("Known", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((string[])field.GetValue(null)!)
            .Where(flag => flag.StartsWith("--", StringComparison.Ordinal))
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull(
            "the test must run within the repository (AgentMemory.slnx not found above {0})",
            AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
