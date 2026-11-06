using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The tool's one terminal writer, on its own (review 8: once every line that prints pack text called Shown, the door matrix no
/// longer saw the writer, so removing it or letting a bare carriage return through failed nothing). It is the second line of
/// defence, and is held to its definition here.
/// </summary>
public sealed class ShownWriterTests
{
    private static string Through(Action<TextWriter> write)
    {
        var inner = new StringWriter { NewLine = "\r\n" };
        using var shown = (TextWriter)Activator.CreateInstance(Writer, inner)!;
        write(shown);
        return inner.ToString();
    }

    // The tool is not referenced as an assembly (its tests run it as a process), so its writer is loaded from its build output.
    private static readonly Type Writer = System.Reflection.Assembly.LoadFrom(Path.Combine(Root(), "tools", "Ampack", "bin",
        typeof(ShownWriterTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration, "net10.0", "Ampack.dll"))
        .GetType("Ampack.ShownWriter", throwOnError: true)!;

    [Fact]
    public void Controls_bidi_and_a_bare_carriage_return_leave_as_escapes_and_line_ends_pass()
    {
        Through(w => w.Write("a\rb\u001b[2J‮c")).Should().Be("a\\u000Db\\u001B[2J\\u202Ec");
        Through(w => w.Write("one\r\ntwo\nthree\tfour")).Should().Be("one\r\ntwo\nthree\tfour", "a CR that ends a line, LF and tab pass");
        Through(w => { w.WriteLine("x\ry"); w.WriteLine(); }).Should().Be("x\\u000Dy\r\n\r\n", "the tool's own line ends are the inner writer's");
        Through(w => w.Write('\r')).Should().Be("\\u000D", "a CR written alone ends no line");
    }

    [Fact]
    public void The_tool_installs_it_on_both_streams()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "tools", "Ampack", "Program.cs"));
        source.Should().Contain("if (Console.Out is not ShownWriter) Console.SetOut(new ShownWriter(Console.Out));")
            .And.Contain("if (Console.Error is not ShownWriter) Console.SetError(new ShownWriter(Console.Error));");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
