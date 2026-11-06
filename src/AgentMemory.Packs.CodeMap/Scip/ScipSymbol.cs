// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace AgentMemory.Packs.CodeMap.Scip;

/// <summary>The suffix that ends a SCIP descriptor (scip.proto, <c>Descriptor.Suffix</c>).</summary>
internal enum ScipSuffix { Namespace, Type, Term, Method, TypeParameter, Parameter, Meta, Macro }

/// <summary>One descriptor of a symbol, and where in the (normalized) symbol text it ends.</summary>
internal sealed record ScipDescriptor(string Name, ScipSuffix Suffix, int End);

/// <summary>
/// A parsed global SCIP symbol: <c>scheme manager name version descriptors</c>, with the version replaced by <c>.</c> so a key
/// survives a release, as the Roslyn reader writes them (<c>scip-dotnet nuget Asm . Ns/Type#</c>). Local symbols (<c>local 1</c>)
/// do not parse: they never leave their document.
/// </summary>
[Experimental("AMEXT001")]
internal sealed record ScipSymbol(string Text, string Manager, string Package, IReadOnlyList<ScipDescriptor> Descriptors)
{
    /// <summary>The symbol up to and including descriptor <paramref name="index"/>.</summary>
    internal string Prefix(int index) => Text[..Descriptors[index].End];

    internal static ScipSymbol? Parse(string symbol)
    {
        if (string.IsNullOrEmpty(symbol) || symbol.StartsWith("local ", StringComparison.Ordinal)) return null;
        // The header: four space-separated parts, a space inside one written as two.
        var at = 0;
        var header = new string[4];
        for (var part = 0; part < 4; part++)
        {
            var token = new StringBuilder();
            while (true)
            {
                if (at >= symbol.Length) return null;
                if (symbol[at] == ' ')
                {
                    if (at + 1 < symbol.Length && symbol[at + 1] == ' ') { token.Append(' '); at += 2; continue; }
                    at++;
                    break;
                }
                token.Append(symbol[at++]);
            }
            header[part] = token.ToString();
        }
        var head = string.Join(' ', ((string[])[header[0], header[1], header[2], "."]).Select(p => p.Replace(" ", "  ", StringComparison.Ordinal))) + " ";
        var offset = head.Length - at;

        var descriptors = new List<ScipDescriptor>();
        while (at < symbol.Length)
        {
            string name;
            if (symbol[at] is '[' or '(')
            {
                var suffix = symbol[at] == '[' ? ScipSuffix.TypeParameter : ScipSuffix.Parameter;
                if (!Bracketed(symbol, ref at, symbol[at] == '[' ? ']' : ')', out name)) return null;
                descriptors.Add(new(name, suffix, at + offset));
                continue;
            }
            if (!Name(symbol, ref at, out name) || at >= symbol.Length) return null;
            ScipSuffix? kind = symbol[at] switch
            {
                '/' => ScipSuffix.Namespace, '#' => ScipSuffix.Type, '.' => ScipSuffix.Term, ':' => ScipSuffix.Meta, '!' => ScipSuffix.Macro,
                '(' => ScipSuffix.Method, _ => null,
            };
            if (kind is null) return null;
            if (kind == ScipSuffix.Method)
            {
                var close = symbol.IndexOf(").", at, StringComparison.Ordinal);
                if (close < 0) return null;
                at = close + 2;
            }
            else at++;
            descriptors.Add(new(name, kind.Value, at + offset));
        }
        return descriptors.Count == 0 ? null : new ScipSymbol(head + symbol[(head.Length - offset)..], header[1], header[2], descriptors);
    }

    // A simple identifier ([A-Za-z0-9_+$-]+) or a backtick-quoted one (`` doubles a backtick).
    private static bool Name(string symbol, ref int at, out string name)
    {
        var text = new StringBuilder();
        if (at < symbol.Length && symbol[at] == '`')
        {
            at++;
            while (at < symbol.Length)
            {
                if (symbol[at] == '`')
                {
                    if (at + 1 < symbol.Length && symbol[at + 1] == '`') { text.Append('`'); at += 2; continue; }
                    at++;
                    name = text.ToString();
                    return true;
                }
                text.Append(symbol[at++]);
            }
            name = "";
            return false;
        }
        while (at < symbol.Length && (char.IsLetterOrDigit(symbol[at]) || symbol[at] is '_' or '+' or '-' or '$')) text.Append(symbol[at++]);
        name = text.ToString();
        return name.Length > 0;
    }

    private static bool Bracketed(string symbol, ref int at, char close, out string name)
    {
        at++;
        if (!Name(symbol, ref at, out name) || at >= symbol.Length || symbol[at] != close) return false;
        at++;
        return true;
    }
}
