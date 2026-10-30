using System.Text;

namespace Ampack;

/// <summary>
/// The one way <c>ampack</c> writes to a terminal (review 7, D3): every control character but line feed and tab, and every
/// bidirectional formatting character, leaves as the six visible characters <c>\uXXXX</c>, whichever line wrote it. A carriage
/// return too, unless a line feed follows it and the two end a line (review 8, F5: a bare one moved the cursor to the start of
/// the line, so pack text overwrote what the tool had printed). A line feed in pack text is the job of <c>Shown</c>, which every
/// line that prints a pack's text calls. Escaping was decided line by line before, so a new line that printed a pack's text could retitle or clear the
/// operator's terminal. <c>\uXXXX</c> is also that character's JSON escape, so <c>--json</c> output keeps its meaning.
/// </summary>
internal sealed class ShownWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    /// <summary>Whether a character is written as its escape.</summary>
    internal static bool Hidden(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\t'))
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
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n') inner.Write('\r');
            else Write(value[i]);
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine() => inner.WriteLine();

    public override void WriteLine(string? value)
    {
        Write(value);
        inner.WriteLine();
    }

    public override void Flush() => inner.Flush();

    // The characters that are always written as they are: printable ASCII, line feed and tab.
    private static readonly System.Buffers.SearchValues<char> Safe =
        System.Buffers.SearchValues.Create("\t\n" + string.Concat(Enumerable.Range(0x20, 0x5F).Select(i => (char)i)));
}
