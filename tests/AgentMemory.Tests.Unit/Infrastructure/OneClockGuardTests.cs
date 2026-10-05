using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// G1 (40.45): the memory path reads time from <c>IClock</c> only, so a host, a test or a replay that sets the clock sets
/// every stamp a recall or a closing compares: <c>created_at</c>, <c>invalidated_at</c>, the end of a change and when it
/// was recorded, live recall's "now", recency. A wall-clock read there would split one moment into two. The few left are
/// outside the memory path, each with its reason below.
/// </summary>
public sealed class OneClockGuardTests
{
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AgentMemory.Core/Stubs/SystemClock.cs"] = "the clock itself",
        ["AgentMemory.Core/Services/SessionIdGenerator.cs"] = "names a per-day session; not a time any memory is stamped with",
        ["AgentMemory.Neo4j/Infrastructure/MigrationRunner.cs"] = "the migration ledger records when a schema change ran",
        ["AgentMemory.Neo4j/Infrastructure/SchemaBootstrapper.cs"] = "a one-off backfill at bootstrap",
        ["AgentMemory.Neo4j/Infrastructure/Neo4jDateTimeHelper.cs"] = "the default for a missing timestamp on malformed data",
    };

    private static readonly Regex WallClock = new(@"\bDateTime(Offset)?\.UtcNow\b", RegexOptions.Compiled);

    [Fact]
    public void The_memory_path_reads_time_from_the_clock_only()
    {
        var src = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "..", "src"));
        var offenders = new List<string>();
        foreach (var project in new[] { "AgentMemory.Core", "AgentMemory.Neo4j" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) ||
                    Allowed.ContainsKey(relative))
                    continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    var comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) code = code[..comment];
                    if (WallClock.IsMatch(code)) offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty("time in the memory path comes from IClock (G1); a new exception needs its reason in Allowed");
    }

    private static string SourceDirectory([CallerFilePath] string? path = null) => Path.GetDirectoryName(path)!;
}
