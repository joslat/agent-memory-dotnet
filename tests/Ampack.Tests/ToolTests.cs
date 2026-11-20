using System.Text.Json;
using Ampack.Tool;
using Xunit;

namespace Ampack.Tests;

/// <summary>The command <c>ampack</c>: its three commands, what they print and the exit codes a pipeline relies on.</summary>
public sealed class ToolTests
{
    private static readonly string Vectors = Path.Combine(Kit.Root, "vectors");
    private static readonly string Trust = Path.Combine(Kit.Root, "trust.json");

    private static (int Exit, string Output) Run(params string[] args)
    {
        using var output = new StringWriter();
        var exit = Program.Run(args, output);
        return (exit, output.ToString());
    }

    [Fact]
    public void Verify_says_verified_with_exit_code_0()
    {
        var (exit, output) = Run("verify", Path.Combine(Vectors, "valid-python-built.ampack"), Trust);
        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Matches("^verified io\\.example\\.notes 1\\.0\\.0 sha256:[0-9a-f]{64}$", lines[0]);
        Assert.Equal("profiles core", lines[1]);
    }

    [Fact]
    public void Verify_says_refused_with_exit_code_1()
    {
        var (exit, output) = Run("verify", Path.Combine(Vectors, "tampered-record.ampack"), Trust);
        Assert.Equal(1, exit);
        Assert.StartsWith("refused: ", output);
        Assert.Contains("does not match META/files.json", output);
    }

    [Fact]
    public void A_missing_file_is_a_refusal_not_a_defect()
    {
        Assert.Equal(1, Run("verify", Path.Combine(Vectors, "no-such.ampack"), Trust).Exit);
        Assert.Equal(1, Run("inspect", Path.Combine(Vectors, "no-such.ampack")).Exit);
    }

    [Theory]
    [InlineData]
    [InlineData("build", "anything")]
    [InlineData("sign", "a.ampack")]
    [InlineData("verify", "only-one-path.ampack")]
    [InlineData("verify", "a.ampack", "trust.json", "--unknown")]
    [InlineData("inspect")]
    public void What_it_does_not_do_is_a_usage_error_with_exit_code_2(params string[] args)
    {
        var (exit, output) = Run(args);
        Assert.Equal(2, exit);
        Assert.Contains("usage: ampack verify", output);
        Assert.Contains("It does not build, sign or certify them.", output);
    }

    [Fact]
    public void Inspect_shows_what_the_file_claims_and_says_it_is_no_verdict()
    {
        var (exit, output) = Run("inspect", Path.Combine(Vectors, "tampered-record.ampack"));
        Assert.Equal(0, exit);
        Assert.Contains("id        io.example.notes", output);
        Assert.Contains("No signature was judged", output);
    }

    [Fact]
    public void Show_verifies_first_then_describes_the_pack()
    {
        var (exit, output) = Run("show", Path.Combine(Vectors, "valid-python-built.ampack"), Trust);
        Assert.Equal(0, exit);
        Assert.StartsWith("verified io.example.notes 1.0.0 sha256:", output);
        Assert.Contains("kind     knowledge", output);
        Assert.Contains("records  ", output);
        Assert.Equal(1, Run("show", Path.Combine(Vectors, "tampered-record.ampack"), Trust).Exit);
    }

    [Fact]
    public void Show_records_prints_one_JSON_object_a_line()
    {
        var (exit, output) = Run("show", Path.Combine(Vectors, "valid-python-built.ampack"), Trust, "--records");
        Assert.Equal(0, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using var record = JsonDocument.Parse(line);
            Assert.Equal("io.example.notes", record.RootElement.GetProperty("pack").GetString());
            Assert.Equal(JsonValueKind.String, record.RootElement.GetProperty("type").ValueKind);
            Assert.Equal(JsonValueKind.String, record.RootElement.GetProperty("key").ValueKind);
        });
    }

    [Fact]
    public void Text_from_a_pack_cannot_fake_a_line()
    {
        Assert.Equal("a?verified x", Program.Shown("a\nverified x"));
        Assert.Equal("a?b", Program.Shown("a\u202Eb"));
        Assert.Equal("ab…", Program.Shown("abc", 2));
    }

    [Fact]
    public void Version_and_help_answer_with_exit_code_0()
    {
        Assert.StartsWith("ampack 0.1.0", Run("--version").Output);
        Assert.Equal(0, Run("--help").Exit);
    }
}
