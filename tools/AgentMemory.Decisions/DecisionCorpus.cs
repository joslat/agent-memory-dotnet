using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentMemory.Decisions;

/// <summary>One retrievable piece of a decision corpus: a section of an ADR, or one entry of a release.</summary>
/// <param name="Id">Short id the answer model cites ("A17").</param>
/// <param name="File">Path relative to the corpus root ("docs/decisions/0042-samples-restructure.md", "CHANGELOG.md").</param>
/// <param name="Heading">The section heading, or for release notes "## [1.6.0] - 2026-09-28 > ### Fixed > Entry title".</param>
/// <param name="Date">The document's or release's date, when it has one.</param>
/// <param name="Text">What retrieval and the answer model see.</param>
internal sealed record DecisionChunk(string Id, string File, string Heading, string? Date, string Text);

/// <summary>Splits decision sources into chunks whose (file, heading) are the locations the fixture labels.</summary>
internal static partial class DecisionCorpus
{
    private const int MaxBody = 1800;

    /// <summary>Every <c>*.md</c> of an ADR folder except templates and the README, one chunk per section.</summary>
    internal static IReadOnlyList<DecisionChunk> FromAdrFolder(string root, string folder, string idPrefix)
    {
        var chunks = new List<DecisionChunk>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, folder), "*.md").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (IsTemplate(name) || name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                continue;
            var file = $"{folder.Replace('\\', '/')}/{name}";
            chunks.AddRange(FromAdr(File.ReadAllText(path), file, idPrefix, chunks.Count));
        }
        return chunks;
    }

    internal static IEnumerable<DecisionChunk> FromAdr(string markdown, string file, string idPrefix, int start)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var index = 0;
        string? date = null, status = null;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (index = 1; index < lines.Length && lines[index].Trim() != "---"; index++)
            {
                var line = lines[index];
                if (line.StartsWith("date:", StringComparison.OrdinalIgnoreCase)) date = line[5..].Trim().Trim('{', '}', ' ');
                if (line.StartsWith("status:", StringComparison.OrdinalIgnoreCase)) status = line[7..].Trim();
            }
            index++;
        }

        string? title = null;
        string heading = "(preamble)";
        var body = new StringBuilder();
        var n = start;
        var inFence = false;
        foreach (var line in lines.Skip(index))
        {
            // A "# comment" inside a code sample is not a heading.
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            var match = HeadingLine().Match(line);
            if (match.Success && !inFence)
            {
                if (body.ToString().Trim().Length > 0) yield return Chunk();
                heading = match.Groups[2].Value.Trim();
                if (match.Groups[1].Value.Length == 1 && title is null) title = heading;
                body.Clear();
                continue;
            }
            body.AppendLine(line);
        }
        if (body.ToString().Trim().Length > 0) yield return Chunk();

        DecisionChunk Chunk()
        {
            var header = $"{file} | {title ?? Path.GetFileNameWithoutExtension(file)} | date: {date ?? "?"} | status: {status ?? "?"}";
            var text = $"{header}\n## {heading}\n{Trim(body.ToString())}";
            return new DecisionChunk($"{idPrefix}{++n}", file, heading, date, text);
        }
    }

    /// <summary>
    /// Release notes, one chunk per entry under a dated release: "## [x.y.z] - date", then "### Section", then "- **Title.** …".
    /// The undated "[Unreleased]" section is not a decision and is skipped.
    /// </summary>
    internal static IReadOnlyList<DecisionChunk> FromChangelog(string markdown, string file, string idPrefix)
    {
        var chunks = new List<DecisionChunk>();
        string? release = null, date = null, section = null, entryTitle = null;
        var entry = new StringBuilder();
        foreach (var line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var match = ReleaseLine().Match(line);
                release = match.Success ? line.Trim() : null;
                date = match.Success ? match.Groups[1].Value : null;
                section = null;
                continue;
            }
            if (release is null) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                section = line.Trim();
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                entryTitle = EntryTitle(line[2..]);
                entry.AppendLine(line[2..]);
                continue;
            }
            if (entryTitle is not null) entry.AppendLine(line.Trim());
        }
        Flush();
        return chunks;

        void Flush()
        {
            if (entryTitle is not null && release is not null)
            {
                var heading = $"{release} > {section ?? "###"} > {entryTitle}";
                chunks.Add(new DecisionChunk($"{idPrefix}{chunks.Count + 1}", file, heading, date,
                    $"{file} | release {release[3..]} | {section?[4..] ?? ""}\n{Trim(entry.ToString())}"));
            }
            entry.Clear();
            entryTitle = null;
        }
    }

    /// <summary>The ADR templates ("adr-template.md"), not an ADR about templates ("0016-custom-prompt-template-formats.md").</summary>
    internal static bool IsTemplate(string name) =>
        name.StartsWith("adr-", StringComparison.OrdinalIgnoreCase) && name.Contains("template", StringComparison.OrdinalIgnoreCase);

    /// <summary>A file at a commit or tag, read without touching the working tree.</summary>
    internal static string GitShow(string repository, string revision, string path)
    {
        var start = new ProcessStartInfo("git", ["-C", repository, "show", $"{revision}:{path}"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git show {revision}:{path} failed: {process.StandardError.ReadToEnd().Trim()}");
        return text;
    }

    /// <summary>The bold title of an entry ("**Title.** body" → "Title."), else its first sentence.</summary>
    internal static string EntryTitle(string entry)
    {
        var bold = BoldTitle().Match(entry);
        if (bold.Success) return bold.Groups[1].Value.Trim();
        var end = entry.IndexOf(". ", StringComparison.Ordinal);
        return (end > 0 ? entry[..(end + 1)] : entry).Trim();
    }

    private static string Trim(string body)
    {
        var text = body.Trim();
        return text.Length <= MaxBody ? text : text[..MaxBody] + " …";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^## \[(?:\d+\.\d+\.\d+[^\]]*)\] - (\d{4}-\d{2}-\d{2})")]
    private static partial Regex ReleaseLine();

    [GeneratedRegex(@"^\*\*(.+?)\*\*")]
    private static partial Regex BoldTitle();
}
