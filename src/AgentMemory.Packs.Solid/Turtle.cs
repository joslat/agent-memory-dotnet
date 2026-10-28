using System.Globalization;
using System.Text;

namespace AgentMemory.Packs.Solid;

/// <summary>An RDF term: an IRI, a blank node, or a literal with its datatype or language.</summary>
internal sealed record RdfTerm(RdfTermKind Kind, string Value, string? Datatype = null, string? Language = null)
{
    internal static RdfTerm Iri(string value) => new(RdfTermKind.Iri, value);
    internal bool IsIri(string value) => Kind == RdfTermKind.Iri && Value == value;
}

internal enum RdfTermKind { Iri, Blank, Literal }

internal sealed record RdfTriple(RdfTerm Subject, string Predicate, RdfTerm Object);

/// <summary>
/// A strict, bounded reader of RDF 1.1 Turtle (federated packs §7): the documents a federated pack meets on the Web, WebID
/// profiles, type indexes and catalogs, which servers write in any valid Turtle. It reads the whole grammar (prefixes and
/// base in both spellings, relative IRIs, prefixed names with escapes, every string form, numbers, booleans, labelled and
/// anonymous blank nodes, property lists, collections) and refuses anything else with the line it stopped at, never
/// guessing. Relative IRIs resolve by RFC 3986 §5.2 on the text itself, so an identity is never changed by normalisation.
/// </summary>
internal sealed class Turtle
{
    internal const int MaximumTriples = 100_000;
    internal const int MaximumNesting = 64;
    internal const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    internal const string Xsd = "http://www.w3.org/2001/XMLSchema#";

    private readonly string _text;
    private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
    private readonly List<RdfTriple> _triples = [];
    private string _base;
    private int _at;
    private int _blank;

    private Turtle(string text, string baseIri) { _text = text; _base = baseIri; }

    /// <summary>Reads <paramref name="text"/> as Turtle, resolving relative IRIs against <paramref name="baseIri"/>.</summary>
    /// <exception cref="InvalidDataException">The text is not Turtle this reader accepts.</exception>
    internal static IReadOnlyList<RdfTriple> Parse(string text, string baseIri)
    {
        var reader = new Turtle(text, baseIri);
        reader.Document();
        return reader._triples;
    }

    private void Document()
    {
        while (true)
        {
            Skip();
            if (_at >= _text.Length) return;
            if (Peek('@')) { Directive(); continue; }
            if (Keyword("PREFIX")) { PrefixBody(sparql: true); continue; }
            if (Keyword("BASE")) { Skip(); _base = Resolve(IriRef()); continue; }
            Triples(0);
            Skip();
            Expect('.');
        }
    }

    private void Directive()
    {
        _at++;
        if (Word("prefix")) PrefixBody(sparql: false);
        else if (Word("base")) { Skip(); _base = Resolve(IriRef()); Skip(); Expect('.'); }
        else throw Fail("an unknown directive");
    }

    private void PrefixBody(bool sparql)
    {
        Skip();
        var start = _at;
        while (_at < _text.Length && _text[_at] != ':' && !char.IsWhiteSpace(_text[_at])) _at++;
        var name = _text[start.._at];
        if (name.Length > 0 && !IsPrefixName(name)) throw Fail("a malformed prefix name");
        Expect(':');
        Skip();
        _prefixes[name] = Resolve(IriRef());
        if (!sparql) { Skip(); Expect('.'); }
    }

    private void Triples(int depth)
    {
        Skip();
        RdfTerm subject;
        if (Peek('['))
        {
            subject = BlankPropertyList(depth);
            Skip();
            if (Peek('.')) return; // "[ ... ] ." is a complete statement
        }
        else if (Peek('(')) subject = Collection(depth);
        else subject = Peek('_') ? BlankLabel() : RdfTerm.Iri(Iri());
        PredicateObjectList(subject, depth);
    }

    private void PredicateObjectList(RdfTerm subject, int depth)
    {
        while (true)
        {
            Skip();
            var predicate = Verb();
            ObjectList(subject, predicate, depth);
            Skip();
            if (!Peek(';')) return;
            while (Peek(';')) { _at++; Skip(); }
            if (Peek('.') || Peek(']') || _at >= _text.Length) return;
        }
    }

    private void ObjectList(RdfTerm subject, string predicate, int depth)
    {
        while (true)
        {
            Skip();
            Add(subject, predicate, Object(depth));
            Skip();
            if (!Peek(',')) return;
            _at++;
        }
    }

    private string Verb()
    {
        if (Peek('a') && _at + 1 < _text.Length && (char.IsWhiteSpace(_text[_at + 1]) || _text[_at + 1] is '<' or '[' or '"' or '\'' or '('))
        {
            _at++;
            return Rdf + "type";
        }
        return Iri();
    }

    private RdfTerm Object(int depth)
    {
        if (depth > MaximumNesting) throw Fail($"nesting deeper than {MaximumNesting}");
        if (Peek('[')) return BlankPropertyList(depth + 1);
        if (Peek('(')) return Collection(depth + 1);
        if (Peek('_')) return BlankLabel();
        if (Peek('"') || Peek('\'')) return Literal();
        if (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] is '+' or '-' or '.')) return Number();
        if (Word("true")) return new RdfTerm(RdfTermKind.Literal, "true", Xsd + "boolean");
        if (Word("false")) return new RdfTerm(RdfTermKind.Literal, "false", Xsd + "boolean");
        return RdfTerm.Iri(Iri());
    }

    private RdfTerm BlankPropertyList(int depth)
    {
        if (depth > MaximumNesting) throw Fail($"nesting deeper than {MaximumNesting}");
        Expect('[');
        var node = NewBlank();
        Skip();
        if (!Peek(']')) PredicateObjectList(node, depth + 1);
        Skip();
        Expect(']');
        return node;
    }

    private RdfTerm Collection(int depth)
    {
        if (depth > MaximumNesting) throw Fail($"nesting deeper than {MaximumNesting}");
        Expect('(');
        var items = new List<RdfTerm>();
        while (true)
        {
            Skip();
            if (Peek(')')) { _at++; break; }
            // The end of the text, or a statement's dot (not a decimal's), before ')': the collection was never closed.
            if (_at >= _text.Length || Peek('.') && (_at + 1 >= _text.Length || !char.IsAsciiDigit(_text[_at + 1]))) throw Fail("an unclosed collection");
            items.Add(Object(depth + 1));
        }
        if (items.Count == 0) return RdfTerm.Iri(Rdf + "nil");
        var head = NewBlank();
        var cell = head;
        for (var i = 0; i < items.Count; i++)
        {
            Add(cell, Rdf + "first", items[i]);
            var next = i == items.Count - 1 ? RdfTerm.Iri(Rdf + "nil") : NewBlank();
            Add(cell, Rdf + "rest", next);
            cell = next;
        }
        return head;
    }

    private RdfTerm BlankLabel()
    {
        if (!(Peek('_') && _at + 1 < _text.Length && _text[_at + 1] == ':')) throw Fail("a blank node label");
        _at += 2;
        var start = _at;
        while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] is '_' or '-' or '.' || _text[_at] > '\u007F' && !char.IsWhiteSpace(_text[_at]))) _at++;
        while (_at > start && _text[_at - 1] == '.') _at--; // a label never ends with '.'
        if (_at == start) throw Fail("an empty blank node label");
        return new RdfTerm(RdfTermKind.Blank, "label:" + _text[start.._at]);
    }

    private RdfTerm NewBlank() => new(RdfTermKind.Blank, "anon:" + (++_blank).ToString(CultureInfo.InvariantCulture));

    private RdfTerm Literal()
    {
        var value = String();
        Skip(); // a language tag or a datatype may follow after white space
        if (Peek('@'))
        {
            _at++;
            var start = _at;
            while (_at < _text.Length && (char.IsAsciiLetterOrDigit(_text[_at]) || _text[_at] == '-')) _at++;
            var tag = _text[start.._at];
            if (tag.Length == 0 || !char.IsAsciiLetter(tag[0])) throw Fail("a malformed language tag");
            return new RdfTerm(RdfTermKind.Literal, value, Rdf + "langString", tag.ToLowerInvariant());
        }
        if (Peek('^'))
        {
            if (_at + 1 >= _text.Length || _text[_at + 1] != '^') throw Fail("'^^' before a datatype");
            _at += 2;
            return new RdfTerm(RdfTermKind.Literal, value, Iri());
        }
        return new RdfTerm(RdfTermKind.Literal, value, Xsd + "string");
    }

    private string String()
    {
        var quote = _text[_at];
        var isLong = _at + 2 < _text.Length && _text[_at + 1] == quote && _text[_at + 2] == quote;
        _at += isLong ? 3 : 1;
        var value = new StringBuilder();
        while (true)
        {
            if (_at >= _text.Length) throw Fail("an unterminated string");
            var c = _text[_at];
            if (isLong && c == quote && _at + 2 < _text.Length && _text[_at + 1] == quote && _text[_at + 2] == quote)
            {
                // A long string may end with one or two of its quote characters before the closing three.
                while (_at + 3 < _text.Length && _text[_at + 3] == quote) { value.Append(quote); _at++; }
                _at += 3;
                return value.ToString();
            }
            if (!isLong && c == quote) { _at++; return value.ToString(); }
            if (!isLong && c is '\n' or '\r') throw Fail("a line break in a short string");
            if (c == '\\') { value.Append(Escape(stringEscape: true)); continue; }
            value.Append(c);
            _at++;
        }
    }

    private string Escape(bool stringEscape)
    {
        _at++;
        if (_at >= _text.Length) throw Fail("an escape at the end");
        var c = _text[_at++];
        switch (c)
        {
            case 'u': return Code(4);
            case 'U': return Code(8);
        }
        if (!stringEscape) throw Fail("an escape other than \\u or \\U in an IRI");
        return c switch
        {
            't' => "\t", 'b' => "\b", 'n' => "\n", 'r' => "\r", 'f' => "\f", '"' => "\"", '\'' => "'", '\\' => "\\",
            _ => throw Fail($"an unknown escape '\\{c}'"),
        };
    }

    private string Code(int digits)
    {
        if (_at + digits > _text.Length || !int.TryParse(_text.AsSpan(_at, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
            || code < 0 || code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
            throw Fail("a malformed \\u or \\U escape");
        _at += digits;
        return char.ConvertFromUtf32(code);
    }

    private RdfTerm Number()
    {
        var start = _at;
        if (_text[_at] is '+' or '-') _at++;
        while (_at < _text.Length && char.IsAsciiDigit(_text[_at])) _at++;
        var datatype = "integer";
        if (_at + 1 < _text.Length && _text[_at] == '.' && char.IsAsciiDigit(_text[_at + 1]))
        {
            _at++;
            while (_at < _text.Length && char.IsAsciiDigit(_text[_at])) _at++;
            datatype = "decimal";
        }
        if (_at < _text.Length && _text[_at] is 'e' or 'E')
        {
            _at++;
            if (_at < _text.Length && _text[_at] is '+' or '-') _at++;
            var digits = _at;
            while (_at < _text.Length && char.IsAsciiDigit(_text[_at])) _at++;
            if (_at == digits) throw Fail("an exponent without digits");
            datatype = "double";
        }
        var text = _text[start.._at];
        if (!text.Any(char.IsAsciiDigit)) throw Fail("a number without digits");
        return new RdfTerm(RdfTermKind.Literal, text, Xsd + datatype);
    }

    private string Iri()
    {
        if (Peek('<')) return Resolve(IriRef());
        // A prefixed name: PN_PREFIX? ':' PN_LOCAL?
        var start = _at;
        while (_at < _text.Length && _text[_at] != ':' && IsNameChar(_text[_at])) _at++;
        var prefix = _text[start.._at];
        if (!Peek(':')) throw Fail("an IRI or a prefixed name");
        if (prefix.Length > 0 && !IsPrefixName(prefix)) throw Fail("a malformed prefix name");
        if (!_prefixes.TryGetValue(prefix, out var ns)) throw Fail($"the undeclared prefix '{prefix}:'");
        _at++;
        var local = new StringBuilder();
        var trailingDots = 0; // unescaped dots at the end so far: an escaped one (\.) belongs to the name
        while (_at < _text.Length)
        {
            var c = _text[_at];
            if (c == '\\')
            {
                if (_at + 1 >= _text.Length || "_~.-!$&'()*+,;=/?#@%".IndexOf(_text[_at + 1], StringComparison.Ordinal) < 0) throw Fail("a malformed escape in a local name");
                local.Append(_text[_at + 1]);
                _at += 2;
                trailingDots = 0;
            }
            else if (c == '%')
            {
                if (_at + 2 >= _text.Length || !char.IsAsciiHexDigit(_text[_at + 1]) || !char.IsAsciiHexDigit(_text[_at + 2])) throw Fail("a malformed percent escape");
                local.Append(_text, _at, 3);
                _at += 3;
                trailingDots = 0;
            }
            else if (IsNameChar(c) || c == ':' || c == '.') { local.Append(c); _at++; trailingDots = c == '.' ? trailingDots + 1 : 0; }
            else break;
        }
        // A local name never ends with an unescaped '.': that dot ends the statement.
        local.Length -= trailingDots;
        _at -= trailingDots;
        return ns + local;
    }

    private string IriRef()
    {
        Expect('<');
        var value = new StringBuilder();
        while (true)
        {
            if (_at >= _text.Length) throw Fail("an unterminated IRI");
            var c = _text[_at];
            if (c == '>') { _at++; return value.ToString(); }
            if (c == '\\') { value.Append(Escape(stringEscape: false)); continue; }
            if (c <= ' ' || c is '<' or '"' or '{' or '}' or '|' or '^' or '`') throw Fail("a character an IRI may not hold");
            value.Append(c);
            _at++;
        }
    }

    private void Add(RdfTerm subject, string predicate, RdfTerm obj)
    {
        if (_triples.Count >= MaximumTriples) throw Fail($"more than {MaximumTriples:N0} triples");
        _triples.Add(new RdfTriple(subject, predicate, obj));
    }

    private string Resolve(string reference) => Iris.Resolve(_base, reference);

    private static bool IsPrefixName(string name) =>
        char.IsLetter(name[0]) && name[^1] != '.' && name.All(c => IsNameChar(c) || c == '.');

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' || c == '·' || c > '\u007F' && !char.IsWhiteSpace(c) && c != '﻿';

    private bool Peek(char c) => _at < _text.Length && _text[_at] == c;

    private void Expect(char c)
    {
        if (!Peek(c)) throw Fail($"'{c}'");
        _at++;
    }

    // A case-sensitive word such as "prefix" after '@', or "true", followed by a delimiter.
    private bool Word(string word)
    {
        if (string.CompareOrdinal(_text, _at, word, 0, word.Length) != 0) return false;
        var end = _at + word.Length;
        if (end < _text.Length && (char.IsLetterOrDigit(_text[end]) || _text[end] is '_' or '-' or ':')) return false;
        _at = end;
        return true;
    }

    // SPARQL-style PREFIX and BASE are case-insensitive.
    private bool Keyword(string word)
    {
        if (_at + word.Length > _text.Length || string.Compare(_text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        var end = _at + word.Length;
        if (end < _text.Length && !char.IsWhiteSpace(_text[end])) return false;
        _at = end;
        return true;
    }

    private void Skip()
    {
        while (_at < _text.Length)
        {
            var c = _text[_at];
            if (char.IsWhiteSpace(c) || c == '﻿' && _at == 0) { _at++; continue; }
            if (c == '#') { while (_at < _text.Length && _text[_at] is not ('\n' or '\r')) _at++; continue; }
            return;
        }
    }

    private InvalidDataException Fail(string what)
    {
        var line = 1 + _text.AsSpan(0, Math.Min(_at, _text.Length)).Count('\n');
        return new InvalidDataException($"Turtle: expected or refused {what} at line {line}.");
    }
}

/// <summary>RFC 3986 §5.2 reference resolution on the text itself: no case folding, no port or percent normalisation.</summary>
internal static class Iris
{
    internal static string Resolve(string baseIri, string reference)
    {
        if (HasScheme(reference)) return RemoveDots(reference);
        var (scheme, authority, path, query) = Split(baseIri);
        var (_, refAuthority, refPath, refQuery, refFragment) = SplitReference(reference);
        string targetAuthority, targetPath, targetQuery;
        if (refAuthority is not null) { targetAuthority = refAuthority; targetPath = RemoveDots(refPath); targetQuery = refQuery ?? ""; }
        else
        {
            targetAuthority = authority;
            if (refPath.Length == 0) { targetPath = path; targetQuery = refQuery ?? query; }
            else
            {
                targetPath = refPath.StartsWith('/') ? RemoveDots(refPath)
                    : RemoveDots(authority.Length > 0 && path.Length == 0 ? "/" + refPath : path[..(path.LastIndexOf('/') + 1)] + refPath);
                targetQuery = refQuery ?? "";
            }
        }
        return scheme + ":" + (targetAuthority.Length > 0 || refAuthority is not null ? "//" + targetAuthority : "") + targetPath
            + (targetQuery.Length > 0 ? "?" + targetQuery : "") + (refFragment is not null ? "#" + refFragment : "");
    }

    private static bool HasScheme(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && char.IsAsciiLetter(text[0]) && text[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            && text.IndexOfAny(['/', '?', '#']) is var stop && (stop < 0 || stop > colon);
    }

    private static (string Scheme, string Authority, string Path, string Query) Split(string iri)
    {
        var hash = iri.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) iri = iri[..hash];
        var colon = iri.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) throw new InvalidDataException("Turtle: the base is not an absolute IRI.");
        var scheme = iri[..colon];
        var rest = iri[(colon + 1)..];
        var authority = "";
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = rest.IndexOfAny(['/', '?'], 2);
            authority = end < 0 ? rest[2..] : rest[2..end];
            rest = end < 0 ? "" : rest[end..];
        }
        var question = rest.IndexOf('?', StringComparison.Ordinal);
        return question < 0 ? (scheme, authority, rest, "") : (scheme, authority, rest[..question], rest[(question + 1)..]);
    }

    private static (string? Scheme, string? Authority, string Path, string? Query, string? Fragment) SplitReference(string reference)
    {
        string? fragment = null, query = null, authority = null;
        var hash = reference.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0) { fragment = reference[(hash + 1)..]; reference = reference[..hash]; }
        var question = reference.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0) { query = reference[(question + 1)..]; reference = reference[..question]; }
        if (reference.StartsWith("//", StringComparison.Ordinal))
        {
            var end = reference.IndexOf('/', 2);
            authority = end < 0 ? reference[2..] : reference[2..end];
            reference = end < 0 ? "" : reference[end..];
        }
        return (null, authority, reference, query, fragment);
    }

    // RFC 3986 §5.2.4.
    private static string RemoveDots(string path)
    {
        if (!path.Contains('.', StringComparison.Ordinal)) return path;
        var prefixEnd = 0;
        var schemeColon = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeColon > 0)
        {
            var pathStart = path.IndexOf('/', schemeColon + 3);
            if (pathStart < 0) return path;
            prefixEnd = pathStart;
        }
        var head = path[..prefixEnd];
        var input = path[prefixEnd..];
        var tailStart = input.IndexOfAny(['?', '#']);
        var tail = tailStart < 0 ? "" : input[tailStart..];
        if (tailStart >= 0) input = input[..tailStart];
        var output = new List<string>();
        var segments = input.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            if (segment == ".") { if (last) output.Add(""); continue; }
            if (segment == "..")
            {
                if (output.Count > 1) output.RemoveAt(output.Count - 1);
                if (last) output.Add("");
                continue;
            }
            output.Add(segment);
        }
        return head + string.Join('/', output) + tail;
    }
}
