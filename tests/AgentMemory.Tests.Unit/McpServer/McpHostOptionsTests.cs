using AgentMemory.McpHost;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>
/// The turnkey host's configuration: defaults, precedence, and the refusals.
/// </summary>
/// <remarks>
/// <para>
/// Option parsing is the part of a host most likely to be quietly wrong and least likely to be caught
/// by running it — a server that starts and answers looks identical whether it read the flag or
/// ignored it. So parsing is separated from running and tested without a database, a provider or a
/// port.
/// </para>
/// <para>
/// The safety-relevant case is <c>--read-only</c>. A typo that is silently ignored produces a fully
/// writable server the operator believes is read-only, which is the one failure this host must not
/// have.
/// </para>
/// </remarks>
public sealed class McpHostOptionsTests
{
    /// <summary>Only the three genuinely required variables, so a test states its own inputs.</summary>
    private static Func<string, string?> Env(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AZURE_OPENAI_ENDPOINT"] = "https://example.openai.azure.com/",
            ["AZURE_OPENAI_API_KEY"] = "key",
            ["NEO4J_PASSWORD"] = "password",
        };
        foreach (var (key, value) in extra) values[key] = value;
        return name => values.TryGetValue(name, out var value) ? value : null;
    }

    private static McpHostOptions Parse(string[] args, Func<string, string?> environment) =>
        McpHostOptions.Parse(args, environment);

    // ── defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void TheDefaultsAreStdioReadWriteAndBootstrapping()
    {
        var options = Parse([], Env());

        options.Transport.Should().Be(McpHostTransport.Stdio);
        options.ReadOnly.Should().BeFalse();
        options.EnableGraphQuery.Should().BeFalse("arbitrary Cypher must be opt-in");
        options.Bootstrap.Should().BeTrue(
            "a missing vector index returns no rows rather than an error, so a server started without "
            + "the schema looks healthy and answers nothing");
        options.LogLevel.Should().Be(LogLevel.Information);
        options.Neo4jUri.Should().Be("bolt://localhost:7687");
        options.Neo4jDatabase.Should().Be("neo4j");
    }

    // ── the refusals ──────────────────────────────────────────────────────

    [Fact]
    public void AnUnknownFlagIsRejectedRatherThanIgnored()
    {
        // THE safety case. A silently-ignored "--read-onlyy" starts a fully writable server that the
        // operator believes is read-only.
        var act = () => Parse(["--read-onlyy"], Env());

        act.Should().Throw<ArgumentException>().WithMessage("*--read-onlyy*");
    }

    [Theory]
    [InlineData("AZURE_OPENAI_ENDPOINT")]
    [InlineData("AZURE_OPENAI_API_KEY")]
    [InlineData("NEO4J_PASSWORD")]
    public void EachRequiredVariableIsRequiredByName(string variable)
    {
        // Named individually so the message says which one, and NEO4J_PASSWORD is required at all --
        // a blank password becomes an authentication failure at the first query, which from an MCP
        // client is indistinguishable from an empty database.
        var without = Env();
        Func<string, string?> missing = name => name == variable ? null : without(name);

        var act = () => Parse([], missing);

        act.Should().Throw<ArgumentException>().WithMessage($"*{variable}*");
    }

    [Fact]
    public void AnUnknownTransportIsRejected()
    {
        var act = () => Parse(["--transport", "grpc"], Env());

        act.Should().Throw<ArgumentException>().WithMessage("*stdio, http*");
    }

    [Fact]
    public void AFlagWithoutItsValueIsRejected()
    {
        var act = () => Parse(["--transport"], Env());

        act.Should().Throw<ArgumentException>().WithMessage("*requires a value*");
    }

    // ── precedence ────────────────────────────────────────────────────────

    [Fact]
    public void AFlagOverridesTheEnvironmentForOrdinarySettings()
    {
        var options = Parse(
            ["--transport", "http"], Env(("AGENT_MEMORY_MCP_TRANSPORT", "stdio")));

        options.Transport.Should().Be(McpHostTransport.Http);
    }

    [Fact]
    public void ReadOnlyIsTheUnionOfFlagAndEnvironmentRatherThanAnOverride()
    {
        // For a safety switch the union is the correct combination: someone who set the variable and
        // someone who passed the flag both asked for read-only, and neither absence cancels the other.
        Parse(["--read-only"], Env()).ReadOnly.Should().BeTrue();
        Parse([], Env(("AGENT_MEMORY_MCP_READ_ONLY", "true"))).ReadOnly.Should().BeTrue();
        Parse(["--read-only"], Env(("AGENT_MEMORY_MCP_READ_ONLY", "false"))).ReadOnly
            .Should().BeTrue("a flag asking for read-only cannot be cancelled by a variable saying otherwise");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    public void TheUsualAffirmativesAllEnableABooleanVariable(string value) =>
        Parse([], Env(("AGENT_MEMORY_MCP_READ_ONLY", value))).ReadOnly.Should().BeTrue();

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("maybe")]
    public void AnythingElseLeavesItOff(string value) =>
        Parse([], Env(("AGENT_MEMORY_MCP_READ_ONLY", value))).ReadOnly.Should().BeFalse();

    [Fact]
    public void ABlankEnvironmentVariableIsTreatedAsAbsent()
    {
        // An exported-but-empty variable is how a container ends up binding to "" and failing at
        // startup with a message about a URL nobody wrote.
        var options = Parse([], Env(("AGENT_MEMORY_MCP_URL", "   "), ("NEO4J_URI", "")));

        options.HttpUrl.Should().Be("http://localhost:5233");
        options.Neo4jUri.Should().Be("bolt://localhost:7687");
    }

    [Fact]
    public void BootstrapIsDisabledByEitherTheFlagOrTheVariable()
    {
        Parse(["--no-bootstrap"], Env()).Bootstrap.Should().BeFalse();
        Parse([], Env(("AGENT_MEMORY_MCP_NO_BOOTSTRAP", "1"))).Bootstrap.Should().BeFalse();
    }
}
