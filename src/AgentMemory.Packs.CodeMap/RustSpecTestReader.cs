// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.CodeMap;

/// <summary>
/// The requirements a Rust test says it validates (CO-2f, test bed (a)), read from its source text: an attribute line
/// <c>#[spec_test(REQ_CF_001)]</c> before the test function names requirement REQ-CF-001 (an identifier stands for the id,
/// "_" for "-"). The code map reads Rust through SCIP, which carries no attributes, so this small text reader sits beside it;
/// each test found feeds <see cref="CodeMapBuilder.AddValidates"/>. Only a line that starts with the attribute counts, so one
/// inside a comment or a string does not; other attributes between it and the function (<c>#[test]</c>, <c>#[rstest]</c>)
/// are passed over, and anything else ends it.
/// </summary>
[Experimental("AMEXT001")]
public static partial class RustSpecTestReader
{
    /// <summary>A test function marked with the requirements it validates.</summary>
    public sealed record SpecTest(string File, string Function, int Line, IReadOnlyList<string> Requirements);

    /// <summary>Every marked test function of one Rust source file, in file order.</summary>
    public static IReadOnlyList<SpecTest> Read(string file, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<SpecTest>();
        var pending = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (SpecAttribute().Match(line) is { Success: true } marked)
            {
                foreach (var identifier in marked.Groups["ids"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (Identifier().IsMatch(identifier) && RequirementId(identifier) is var id && !pending.Contains(id, StringComparer.Ordinal))
                        pending.Add(id);
                continue;
            }
            if (pending.Count == 0) continue;
            if (line.Length == 0 || line.StartsWith("#[", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (Function().Match(line) is { Success: true } function)
                found.Add(new(file, function.Groups["name"].Value, i + 1, [.. pending]));
            pending.Clear();
        }
        return found;
    }

    /// <summary>The requirement id an identifier stands for: "REQ_CB_analyzer_003" is "REQ-CB-analyzer-003".</summary>
    public static string RequirementId(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return identifier.Trim().Replace('_', '-');
    }

    [GeneratedRegex(@"^#\[spec_test\((?<ids>[^)]*)\)\]")]
    private static partial Regex SpecAttribute();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?fn\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Function();
}
