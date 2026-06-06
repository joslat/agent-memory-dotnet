using FluentAssertions;
using AgentMemory.Cli;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class CliArgsTests
{
    [Fact]
    public void Parse_ExtractsCommandAsFirstBareToken()
    {
        var cli = CliArgs.Parse(["migrate", "--uri", "bolt://x:7687"]);

        cli.Command.Should().Be("migrate");
    }

    [Fact]
    public void Parse_NoCommand_LeavesCommandNull()
    {
        CliArgs.Parse(["--apply"]).Command.Should().BeNull();
    }

    [Fact]
    public void Parse_BareFlag_IsPresentWithNullValue()
    {
        var cli = CliArgs.Parse(["consolidate", "--apply"]);

        cli.HasFlag("apply").Should().BeTrue();
        cli.Get("apply").Should().BeNull();
    }

    [Fact]
    public void Parse_KeyValue_SpaceSeparated()
    {
        var cli = CliArgs.Parse(["decay", "--session", "user-42"]);

        cli.Get("session").Should().Be("user-42");
    }

    [Fact]
    public void Parse_KeyValue_EqualsSeparated()
    {
        var cli = CliArgs.Parse(["migrate", "--uri=bolt://db:7687"]);

        cli.Get("uri").Should().Be("bolt://db:7687");
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnOptionNames()
    {
        var cli = CliArgs.Parse(["consolidate", "--APPLY"]);

        cli.HasFlag("apply").Should().BeTrue();
    }

    [Fact]
    public void Get_UnknownOption_ReturnsNull()
    {
        CliArgs.Parse(["migrate"]).Get("nope").Should().BeNull();
    }
}
