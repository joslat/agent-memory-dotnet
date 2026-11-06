// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentMemory.Packs.CodeMap.Scip;

/// <summary>One source range: zero-based lines and characters, end exclusive.</summary>
internal readonly record struct ScipRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter)
{
    internal static ScipRange? FromPacked(List<int> values) => values.Count switch
    {
        3 => new ScipRange(values[0], values[1], values[0], values[2]),
        4 => new ScipRange(values[0], values[1], values[2], values[3]),
        _ => null,
    };
}

/// <summary>A symbol at a range of a document, with its roles (definition, test) and, when the indexer gives it, the range of
/// the code around it (a function's body for its definition).</summary>
internal sealed record ScipOccurrence(string Symbol, int Roles, ScipRange Range, ScipRange? Enclosing)
{
    internal const int DefinitionRole = 0x1;
    internal const int TestRole = 0x20;
    internal bool IsDefinition => (Roles & DefinitionRole) != 0;
}

/// <summary>What a document says about a symbol it defines.</summary>
internal sealed record ScipSymbolInformation(string Symbol, int Kind, string DisplayName, string EnclosingSymbol);

/// <summary>One indexed source file.</summary>
internal sealed record ScipDocument(string RelativePath, string Language, IReadOnlyList<ScipOccurrence> Occurrences,
    IReadOnlyList<ScipSymbolInformation> Symbols);

/// <summary>A SCIP index as the code map reads it: its documents and the tool that wrote it.</summary>
internal sealed record ScipIndex(string Tool, string ProjectRoot, IReadOnlyList<ScipDocument> Documents);

/// <summary>
/// Reads a SCIP index (<c>index.scip</c>, protobuf; schema github.com/sourcegraph/scip, scip.proto) without a protobuf
/// dependency: only the fields the code map uses, everything else skipped. Both range encodings are read: the packed
/// <c>range</c> and <c>enclosing_range</c> (deprecated) and the typed single- and multi-line ones that replace them.
/// </summary>
[Experimental("AMEXT001")]
internal static class ScipIndexReader
{
    internal static ScipIndex Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var reader = new Wire(buffer.GetBuffer(), 0, (int)buffer.Length, new Dictionary<string, string>(StringComparer.Ordinal));
        string tool = "", root = "";
        var documents = new List<ScipDocument>();
        while (reader.Next(out var field, out var type))
        {
            if (field == 1 && type == 2) (tool, root) = Metadata(reader.Message());
            else if (field == 2 && type == 2) documents.Add(Document(reader.Message()));
            else reader.Skip(type);
        }
        return new ScipIndex(tool, root, documents);
    }

    private static (string Tool, string Root) Metadata(Wire wire)
    {
        string tool = "", root = "";
        while (wire.Next(out var field, out var type))
        {
            if (field == 2 && type == 2)
            {
                var info = wire.Message();
                while (info.Next(out var f, out var t)) { if (f == 1 && t == 2) tool = info.String(); else info.Skip(t); }
            }
            else if (field == 3 && type == 2) root = wire.String();
            else wire.Skip(type);
        }
        return (tool, root);
    }

    private static ScipDocument Document(Wire wire)
    {
        string path = "", language = "";
        var occurrences = new List<ScipOccurrence>();
        var symbols = new List<ScipSymbolInformation>();
        while (wire.Next(out var field, out var type))
        {
            switch (field, type)
            {
                case (1, 2): path = wire.String(); break;
                case (2, 2): if (Occurrence(wire.Message()) is { } occurrence) occurrences.Add(occurrence); break;
                case (3, 2): symbols.Add(Symbol(wire.Message())); break;
                case (4, 2): language = wire.String(); break;
                default: wire.Skip(type); break;
            }
        }
        return new ScipDocument(path, language, occurrences, symbols);
    }

    private static ScipOccurrence? Occurrence(Wire wire)
    {
        var symbol = "";
        var roles = 0;
        List<int> packed = [], packedEnclosing = [];
        ScipRange? typed = null, typedEnclosing = null;
        while (wire.Next(out var field, out var type))
        {
            switch (field, type)
            {
                case (1, 2): wire.PackedInts(packed); break;
                case (1, 0): packed.Add((int)wire.Varint()); break;
                case (2, 2): symbol = wire.String(); break;
                case (3, 0): roles = (int)wire.Varint(); break;
                case (7, 2): wire.PackedInts(packedEnclosing); break;
                case (7, 0): packedEnclosing.Add((int)wire.Varint()); break;
                case (8, 2): typed = Single(wire.Message()); break;
                case (9, 2): typed = Multi(wire.Message()); break;
                case (10, 2): typedEnclosing = Single(wire.Message()); break;
                case (11, 2): typedEnclosing = Multi(wire.Message()); break;
                default: wire.Skip(type); break;
            }
        }
        var range = typed ?? ScipRange.FromPacked(packed);
        return range is { } r && symbol.Length > 0 ? new ScipOccurrence(symbol, roles, r, typedEnclosing ?? ScipRange.FromPacked(packedEnclosing)) : null;
    }

    private static ScipRange Single(Wire wire)
    {
        int line = 0, start = 0, end = 0;
        while (wire.Next(out var field, out var type))
            switch (field, type)
            {
                case (1, 0): line = (int)wire.Varint(); break;
                case (2, 0): start = (int)wire.Varint(); break;
                case (3, 0): end = (int)wire.Varint(); break;
                default: wire.Skip(type); break;
            }
        return new ScipRange(line, start, line, end);
    }

    private static ScipRange Multi(Wire wire)
    {
        int startLine = 0, start = 0, endLine = 0, end = 0;
        while (wire.Next(out var field, out var type))
            switch (field, type)
            {
                case (1, 0): startLine = (int)wire.Varint(); break;
                case (2, 0): start = (int)wire.Varint(); break;
                case (3, 0): endLine = (int)wire.Varint(); break;
                case (4, 0): end = (int)wire.Varint(); break;
                default: wire.Skip(type); break;
            }
        return new ScipRange(startLine, start, endLine, end);
    }

    private static ScipSymbolInformation Symbol(Wire wire)
    {
        string symbol = "", display = "", enclosing = "";
        var kind = 0;
        while (wire.Next(out var field, out var type))
            switch (field, type)
            {
                case (1, 2): symbol = wire.String(); break;
                case (5, 0): kind = (int)wire.Varint(); break;
                case (6, 2): display = wire.String(); break;
                case (8, 2): enclosing = wire.String(); break;
                default: wire.Skip(type); break;
            }
        return new ScipSymbolInformation(symbol, kind, display, enclosing);
    }

    /// <summary>The protobuf wire format over a slice of bytes; strings are interned, an index repeats its symbols often.</summary>
    private sealed class Wire(byte[] bytes, int start, int end, Dictionary<string, string> strings)
    {
        private int _at = start;

        public bool Next(out int field, out int type)
        {
            field = type = 0;
            if (_at >= end) return false;
            var tag = Varint();
            field = (int)(tag >> 3);
            type = (int)(tag & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_at >= end) throw new InvalidDataException("A SCIP index ends inside a number.");
                var b = bytes[_at++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new InvalidDataException("A SCIP index holds a number longer than 64 bits.");
        }

        private int Length()
        {
            var length = Varint();
            if (length > (ulong)(end - _at)) throw new InvalidDataException("A SCIP index holds a field longer than what remains.");
            return (int)length;
        }

        public Wire Message()
        {
            var length = Length();
            var inner = new Wire(bytes, _at, _at + length, strings);
            _at += length;
            return inner;
        }

        public string String()
        {
            var length = Length();
            var text = Encoding.UTF8.GetString(bytes, _at, length);
            _at += length;
            if (strings.TryGetValue(text, out var known)) return known;
            strings[text] = text;
            return text;
        }

        public void PackedInts(List<int> values)
        {
            var length = Length();
            var stop = _at + length;
            var inner = new Wire(bytes, _at, stop, strings);
            while (inner._at < stop) values.Add((int)inner.Varint());
            _at = stop;
        }

        public void Skip(int type)
        {
            switch (type)
            {
                case 0: Varint(); break;
                case 1: _at += 8; break;
                case 2:
                    // Not `_at += Length()`: that reads _at before Length() moves it past the length's own bytes.
                    var length = Length();
                    _at += length;
                    break;
                case 5: _at += 4; break;
                default: throw new InvalidDataException($"A SCIP index holds an unsupported wire type {type}.");
            }
            if (_at > end) throw new InvalidDataException("A SCIP index ends inside a field.");
        }
    }
}
