using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

// A bounded MATCH/WHERE/SET grammar. Every graph binding is statically scoped; expressions are
// scalar and cannot introduce graph reads, procedures, user functions or a new binding scope.
// Accepted updates need transactional statement progress: an increment is not replay-idempotent.
internal static class ModuleBackfillStatement
{
    private static readonly FrozenSet<string> Functions = new[]
    {
        "coalesce", "abs", "ceil", "floor", "round", "size", "tostring", "tointeger", "tofloat",
        "toboolean", "trim", "ltrim", "rtrim", "tolower", "toupper", "replace", "substring",
        "date", "datetime", "localdatetime", "duration"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Number = new(@"\A[0-9]+(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static ModuleMigrationOperation Parse(ModuleCypherStatement statement) => new Parser(statement).Parse();

    private sealed class Parser(ModuleCypherStatement statement)
    {
        private readonly ModuleCypherReader _reader = new(statement.Tokens);
        private readonly Dictionary<string, bool> _aliases = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _labels = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _relations = new(StringComparer.Ordinal);
        private readonly SortedSet<string> _writes = new(StringComparer.Ordinal);
        private int _depth;

        internal ModuleMigrationOperation Parse()
        {
            _reader.Word("MATCH");
            do { Pattern(); } while (_reader.TakeSymbol(","));
            if (_reader.TakeWord("WHERE")) Expression();
            _reader.Word("SET");
            do
            {
                var property = PropertyReference();
                _writes.Add(property);
                _reader.Symbol("=");
                Expression();
            } while (_reader.TakeSymbol(","));
            if (!_reader.AtEnd) throw Invalid("unsupported clause or trailing expression");
            return new()
            {
                Text = statement.Text, Labels = _labels.ToList().AsReadOnly(),
                RelationshipTypes = _relations.ToList().AsReadOnly(), WrittenProperties = _writes.ToList().AsReadOnly()
            };
        }

        private void Pattern()
        {
            Node();
            while (_reader.Current is { Kind: ModuleCypherTokenKind.Symbol, Text: "-" or "<" })
            {
                var incoming = _reader.TakeSymbol("<");
                _reader.Symbol("-");
                _reader.Symbol("[");
                Binding(relationship: true);
                _reader.Symbol("]");
                _reader.Symbol("-");
                var outgoing = _reader.TakeSymbol(">");
                if (incoming && outgoing) throw Invalid("relationship cannot point in both directions");
                Node();
            }
        }

        private void Node()
        {
            _reader.Symbol("(");
            Binding(relationship: false);
            _reader.Symbol(")");
        }

        private void Binding(bool relationship)
        {
            var alias = _reader.Identifier();
            if (_aliases.TryGetValue(alias, out var existing) && existing != relationship)
                throw Invalid("binding changes entity kind");
            _aliases[alias] = relationship;
            _reader.Symbol(":");
            (relationship ? _relations : _labels).Add(_reader.Identifier());
            if (!relationship)
                while (_reader.TakeSymbol(":")) _labels.Add(_reader.Identifier());
        }

        private string PropertyReference()
        {
            var alias = _reader.Identifier();
            if (!_aliases.ContainsKey(alias)) throw Invalid("property uses an unbound variable");
            _reader.Symbol(".");
            return _reader.Identifier();
        }

        private void Expression(int minimumPrecedence = 0)
        {
            if (++_depth > 64) throw Invalid("expression nesting exceeds 64 levels");
            Atom();
            while (true)
            {
                var precedence = Precedence(_reader.Current);
                if (precedence < minimumPrecedence) break;
                var op = _reader.Current!.Text.ToUpperInvariant();
                _reader.Advance();
                if (op == "IS")
                {
                    _reader.TakeWord("NOT");
                    _reader.Word("NULL");
                    continue;
                }
                if (op is "<" or ">")
                {
                    if (!_reader.TakeSymbol("=" ) && op == "<") _reader.TakeSymbol(">");
                }
                else if (op == "!") _reader.Symbol("=");
                Expression(precedence + 1);
            }
            _depth--;
        }

        private void Atom()
        {
            if (_reader.TakeWord("NOT") || _reader.TakeSymbol("+") || _reader.TakeSymbol("-"))
            { Expression(7); return; }
            if (_reader.TakeSymbol("(")) { Expression(); _reader.Symbol(")"); return; }
            if (_reader.TakeSymbol("["))
            {
                if (!_reader.TakeSymbol("]"))
                {
                    do { Expression(); } while (_reader.TakeSymbol(","));
                    _reader.Symbol("]");
                }
                return;
            }
            var token = _reader.Current ?? throw Invalid("missing expression");
            if (token.Kind == ModuleCypherTokenKind.Literal) { _reader.Advance(); return; }
            if (_reader.TakeWord("TRUE") || _reader.TakeWord("FALSE") || _reader.TakeWord("NULL")) return;
            if (token.Kind == ModuleCypherTokenKind.Word && Number.IsMatch(token.Text))
            {
                _reader.Advance();
                if (_reader.TakeSymbol("."))
                {
                    if (_reader.Current is not { Kind: ModuleCypherTokenKind.Word } fraction || !Number.IsMatch(fraction.Text))
                        throw Invalid("invalid numeric fraction");
                    _reader.Advance();
                }
                return;
            }
            if (token.Kind == ModuleCypherTokenKind.Word && Functions.Contains(token.Text))
            {
                _reader.Advance();
                _reader.Symbol("(");
                if (!_reader.TakeSymbol(")"))
                {
                    do { Expression(); } while (_reader.TakeSymbol(","));
                    _reader.Symbol(")");
                }
                return;
            }
            PropertyReference();
        }

        private static int Precedence(ModuleCypherToken? token) => token?.Kind switch
        {
            ModuleCypherTokenKind.Word => token.Text.ToUpperInvariant() switch
            { "OR" => 0, "XOR" => 1, "AND" => 2, "IS" or "IN" => 3, _ => -1 },
            ModuleCypherTokenKind.Symbol => token.Text switch
            { "=" or "<" or ">" or "!" => 3, "+" or "-" => 4, "*" or "/" or "%" => 5, "^" => 6, _ => -1 },
            _ => -1
        };
    }

    private static FormatException Invalid(string message) => new("Module backfill: " + message + ".");
}
