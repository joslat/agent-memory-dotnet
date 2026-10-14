using System.Text;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal enum ModuleCypherTokenKind { Word, QuotedIdentifier, Literal, Symbol }
internal sealed record ModuleCypherToken(ModuleCypherTokenKind Kind, string Text);
internal sealed record ModuleCypherStatement(string Text, IReadOnlyList<ModuleCypherToken> Tokens);

// A bounded lexical reader, not a Cypher grammar or authorization boundary. The compiler must
// validate allowed operations, exact owned shapes and idempotency before any statement executes.
internal static class ModuleCypherScript
{
    internal const int MaximumCharacters = 262144;

    internal static IReadOnlyList<string> Split(string source) =>
        Parse(source).Select(statement => statement.Text).ToList().AsReadOnly();

    internal static IReadOnlyList<ModuleCypherStatement> Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumCharacters) throw Invalid("script exceeds 256 Ki characters");
        var statements = new List<ModuleCypherStatement>();
        var tokens = new List<ModuleCypherToken>();
        var text = new StringBuilder();
        var nesting = new Stack<char>();
        var position = 0;
        while (position < source.Length)
        {
            var c = source[position];
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n')) throw Invalid("unexpected control character");
            if (char.IsWhiteSpace(c)) { text.Append(c); position++; continue; }
            if (At(source, position, "//"))
            {
                position += 2;
                while (position < source.Length && source[position] is not ('\r' or '\n')) position++;
                text.Append(' ');
                continue;
            }
            if (At(source, position, "/*"))
            {
                SkipComment(source, ref position);
                text.Append(' ');
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var start = position;
                var value = ReadQuoted(source, ref position, c);
                text.Append(source, start, position - start);
                tokens.Add(new(c == '`' ? ModuleCypherTokenKind.QuotedIdentifier : ModuleCypherTokenKind.Literal, value));
                continue;
            }
            if (c == ';')
            {
                if (nesting.Count != 0) throw Invalid("statement terminator inside an unclosed expression");
                Flush();
                position++;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                var start = position++;
                while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
                var value = source[start..position];
                text.Append(value);
                tokens.Add(new(ModuleCypherTokenKind.Word, value));
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                if (nesting.Count == 64) throw Invalid("expression nesting exceeds 64 levels");
                nesting.Push(c);
            }
            if (c is ')' or ']' or '}')
            {
                var expected = c switch { ')' => '(', ']' => '[', _ => '{' };
                if (!nesting.TryPop(out var open) || open != expected) throw Invalid("unbalanced expression delimiters");
            }
            text.Append(c);
            tokens.Add(new(ModuleCypherTokenKind.Symbol, c.ToString()));
            position++;
        }
        if (nesting.Count != 0) throw Invalid("unclosed expression delimiter");
        Flush();
        return statements.AsReadOnly();

        void Flush()
        {
            if (tokens.Count != 0) statements.Add(new(text.ToString().Trim(), tokens.ToList().AsReadOnly()));
            tokens.Clear();
            text.Clear();
        }
    }

    private static string ReadQuoted(string source, ref int position, char quote)
    {
        position++;
        var value = new StringBuilder();
        while (position < source.Length)
        {
            var c = source[position++];
            if (c == '\\')
            {
                // Reject escaped identifiers rather than inspect a spelling that Neo4j decodes to
                // a different label. Doubled backticks below are the supported identifier escape.
                if (quote == '`') throw Invalid("backslash escapes in identifiers are not supported");
                if (position == source.Length) throw Invalid("unfinished quoted escape");
                value.Append(c).Append(source[position++]);
            }
            else if (c == quote)
            {
                if (position < source.Length && source[position] == quote)
                { value.Append(quote); position++; }
                else return value.ToString();
            }
            else value.Append(c);
        }
        throw Invalid("unterminated quoted value or identifier");
    }

    private static void SkipComment(string source, ref int position)
    {
        var depth = 1;
        position += 2;
        while (position < source.Length)
        {
            if (At(source, position, "/*"))
            {
                if (++depth > 64) throw Invalid("comment nesting exceeds 64 levels");
                position += 2;
            }
            else if (At(source, position, "*/"))
            {
                position += 2;
                if (--depth == 0) return;
            }
            else position++;
        }
        throw Invalid("unterminated block comment");
    }

    private static bool At(string source, int position, string value) =>
        source.AsSpan(position).StartsWith(value.AsSpan(), StringComparison.Ordinal);

    private static FormatException Invalid(string message) => new("Module migration: " + message + ".");
}
