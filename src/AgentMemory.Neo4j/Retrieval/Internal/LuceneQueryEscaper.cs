using System.Text;

namespace AgentMemory.Neo4j.Retrieval.Internal;

/// <summary>
/// Escapes Lucene query-syntax metacharacters so a free-text recall query is matched as literal text by
/// Neo4j's fulltext index, rather than being (mis)interpreted as Lucene operators. Without this, a perfectly
/// ordinary query — <c>"C++ vs Rust: which is faster?"</c>, an unbalanced quote/paren, etc. — either throws a
/// Lucene parse error (the driver surfaces it as an unhandled exception) or silently changes recall (a leading
/// <c>-</c> becomes NOT). Used on the raw-query path (<c>filterStopWords = false</c>); the stop-word path is
/// already safe because it tokenizes to <c>\w+</c> and drops every metacharacter.
/// </summary>
internal static class LuceneQueryEscaper
{
    // The Lucene special characters: + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
    private static readonly HashSet<char> Special = new(
        new[] { '+', '-', '!', '(', ')', '{', '}', '[', ']', '^', '"', '~', '*', '?', ':', '\\', '/', '&', '|' });

    /// <summary>Backslash-escapes every Lucene metacharacter in <paramref name="query"/>.</summary>
    public static string Escape(string? query)
    {
        if (string.IsNullOrEmpty(query))
            return query ?? string.Empty;

        var sb = new StringBuilder(query.Length + 8);
        foreach (var c in query)
        {
            if (Special.Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
