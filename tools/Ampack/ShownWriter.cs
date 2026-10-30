using System.Text;

namespace Ampack;

/// <summary>
/// The one way <c>ampack</c> writes to a terminal (review 7, D3): every control character but line feed, carriage return and
/// tab, and every bidirectional formatting character, leaves as the six visible characters <c>\uXXXX</c>, whichever line
/// wrote it. Escaping was decided line by line before, so a new line that printed a pack's text could retitle or clear the
/// operator's terminal. <c>\uXXXX</c> is also that character's JSON escape, so <c>--json</c> output keeps its meaning.
/// </summary>
internal sealed class ShownWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    /// <summary>Whether a character is written as its escape.</summary>
    internal static bool Hidden(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
        || c is '؜' or '‎' or '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    public override void Write(char value)
    {
        if (Hidden(value)) inner.Write($"\\u{(int)value:X4}");
        else inner.Write(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        if (value.AsSpan().IndexOfAnyExcept(Safe) < 0) { inner.Write(value); return; }
        foreach (var c in value) Write(c);
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value)
    {
        Write(value);
        inner.WriteLine();
    }

    public override void Flush() => inner.Flush();

    // The characters that are always written as they are: printable ASCII, line feed, carriage return and tab.
    private static readonly System.Buffers.SearchValues<char> Safe =
        System.Buffers.SearchValues.Create("\t\n\r" + string.Concat(Enumerable.Range(0x20, 0x5F).Select(i => (char)i)));
}
