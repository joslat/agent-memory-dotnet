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

    [Theory]
    [InlineData("-42")]       // owner id leading with '-'
    [InlineData("-s3cret")]   // secret leading with '-'
    public void Parse_SpaceSeparatedValue_LeadingDash_IsConsumedAsValue(string value)
    {
        // A value beginning with '-' must NOT be mistaken for a new option — otherwise `--owner -42` drops
        // the value to null, silently widening a scoped destructive prune to ALL owners, and `--password
        // -s3cret` discards the credential.
        CliArgs.Parse(["decay", "--owner", value]).Get("owner").Should().Be(value);
    }

    [Fact]
    public void Parse_NextLongOption_IsNotConsumedAsValue()
    {
        // A genuine following long option ("--...") is still treated as a separate option, so the first
        // option remains a value-less bare flag.
        var cli = CliArgs.Parse(["consolidate", "--apply", "--dry-run"]);

        cli.HasFlag("apply").Should().BeTrue();
        cli.Get("apply").Should().BeNull();
        cli.HasFlag("dry-run").Should().BeTrue();
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
