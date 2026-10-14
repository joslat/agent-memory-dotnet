namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal sealed class ModuleCypherReader(IReadOnlyList<ModuleCypherToken> tokens)
{
    private int _position;
    internal bool AtEnd => _position == tokens.Count;
    internal ModuleCypherToken? Current => AtEnd ? null : tokens[_position];
    internal void Advance() { if (AtEnd) throw Invalid("unexpected end of statement"); _position++; }
    private static FormatException Invalid(string message) => new("Module migration syntax: " + message + ".");

    internal bool TakeWord(string value) => Take(ModuleCypherTokenKind.Word, value, StringComparison.OrdinalIgnoreCase);
    internal bool TakeSymbol(string value) => Take(ModuleCypherTokenKind.Symbol, value, StringComparison.Ordinal);

    internal void Word(string value)
    {
        if (!TakeWord(value)) throw Invalid("expected keyword " + value);
    }

    internal void Symbol(string value)
    {
        if (!TakeSymbol(value)) throw Invalid("expected symbol " + value);
    }

    internal string Identifier()
    {
        if (AtEnd) throw Invalid("expected identifier");
        var token = tokens[_position];
        if (token.Text.Length == 0 || (token.Kind != ModuleCypherTokenKind.QuotedIdentifier
            && (token.Kind != ModuleCypherTokenKind.Word || !(char.IsLetter(token.Text[0]) || token.Text[0] == '_'))))
            throw Invalid("expected a static identifier");
        _position++;
        return token.Text;
    }

    private bool Take(ModuleCypherTokenKind kind, string value, StringComparison comparison)
    {
        if (AtEnd || tokens[_position].Kind != kind || !string.Equals(tokens[_position].Text, value, comparison)) return false;
        _position++;
        return true;
    }
}
