using System.Text;

namespace AgentMemory.Extraction.Llm;

/// <summary>
/// Finds the words a memory writer operation quotes in the message it rests on (#249). The writer's prompt asks every
/// operation to quote "the exact words of the last message it rests on"; this checks that it did.
/// </summary>
/// <remarks>
/// <para>
/// Only what copying text can change is forgiven: runs of whitespace (a quote across a line break), typographic quotation
/// marks and apostrophes, and runs of dashes. Then an exact match, else a case-folded one. No fuzzy matching, and never
/// across messages: a paraphrase is not a quote.
/// </para>
/// <para>
/// Measured on the Bruna scenario's busier history (GLM-5.3-Flash, 7 Nov 2026): 166 of 166 writer quotes were found
/// exactly, so requiring them costs a well-behaved writer nothing.
/// </para>
/// </remarks>
internal static class MemoryWriterQuotes
{
    /// <summary>
    /// Where <paramref name="quote"/> stands in <paramref name="message"/>: the start offset (inclusive) and the end
    /// offset (exclusive) in the message's own characters; null when it is not there.
    /// </summary>
    internal static (int Start, int End)? Locate(string? message, string? quote)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrWhiteSpace(quote)) return null;
        var (text, map) = Normalize(message);
        var (needle, _) = Normalize(quote);
        needle = needle.Trim();
        if (needle.Length == 0) return null;

        var at = text.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var end = at + needle.Length;
        return (map[at], end < map.Count ? map[end] : message.Length);
    }

    /// <summary>
    /// The text with whitespace runs as one space, typographic quotes and apostrophes as straight ones and dash runs as one
    /// hyphen, and for each of its characters the offset of the original character it came from.
    /// </summary>
    private static (string Text, List<int> Map) Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && builder[^1] == ' ') continue;
                c = ' ';
            }
            else if (c is '‘' or '’' or '‛' or '′') c = '\'';
            else if (c is '“' or '”' or '‟' or '″') c = '"';
            else if (c is '-' or '‐' or '‑' or '‒' or '–' or '—' or '―' or '−')
            {
                if (builder.Length > 0 && builder[^1] == '-') continue;
                c = '-';
            }

            builder.Append(c);
            map.Add(i);
        }

        return (builder.ToString(), map);
    }
}
