#pragma warning disable AMEXT001
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Context.Traversal;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// Review 7, W1 and W2: the matcher's work is bounded inside one stored value, on the largest input the format admits, and each
/// normative function is checked against its definition over a generated class, not on chosen examples. The path's closed form
/// equals the loop the spec defined; the glob matcher equals the regex it replaced, which threw past the engine's size limit.
/// </summary>
public sealed class AnchorMatcherWorkTests
{
    private static readonly char[] AsciiSpace = [' ', '\t', '\r', '\n', '\f', '\v'];

    // The definition: trimmed, '\' as '/', every leading "./" then every leading "/", repeated until nothing changes.
    private static string PathOracle(string value)
    {
        string path = value, previous;
        do
        {
            previous = path;
            path = path.Trim(AsciiSpace).Replace('\\', '/');
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            while (path.StartsWith('/')) path = path[1..];
        }
        while (path != previous);
        return path;
    }

    // The regex the glob matcher replaced, over folded text.
    private static bool GlobOracle(string pattern, string text)
    {
        var regex = new StringBuilder(@"\A");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(slash ? @"(?:[\s\S]*/)?" : @"[\s\S]*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }
        return Regex.IsMatch(text, regex.Append(@"\z").ToString(), RegexOptions.CultureInvariant);
    }

    [Fact]
    public void The_path_in_one_pass_equals_the_loop_the_format_defines_on_every_generated_value()
    {
        var random = new Random(20261030);
        const string Alphabet = "./\\ \t\f\vaB";
        for (var n = 0; n < 20_000; n++)
        {
            var value = new string([.. Enumerable.Range(0, random.Next(0, 14)).Select(_ => Alphabet[random.Next(Alphabet.Length)])]);
            AnchorMatcher.Normalized(value).Should().Be(PathOracle(value), $"for \"{Regex.Escape(value)}\"");
            AnchorMatcher.Normalized(AnchorMatcher.Normalized(value)).Should().Be(AnchorMatcher.Normalized(value), "a normalized path normalizes to itself");
        }
    }

    [Fact]
    public void The_glob_matcher_equals_the_regex_it_replaced_on_every_generated_pair()
    {
        var random = new Random(20261031);
        const string Patterns = "ab/*?.", Texts = "ab/.";
        for (var n = 0; n < 20_000; n++)
        {
            var pattern = new string([.. Enumerable.Range(0, random.Next(1, 10)).Select(_ => Patterns[random.Next(Patterns.Length)])]);
            var text = new string([.. Enumerable.Range(0, random.Next(1, 10)).Select(_ => Texts[random.Next(Texts.Length)])]);
            long work = 0;
            AnchorMatcher.GlobPattern.Of(pattern).Matches(text, ref work).Should().Be(GlobOracle(pattern, text), $"'{pattern}' against '{text}'");
        }
    }

    [Fact]
    public void W1_a_run_of_dot_slashes_and_white_space_normalizes_in_linear_time()
    {
        // 20,000 alternations took 20,000 passes, each copying the value twice.
        var value = string.Concat(Enumerable.Repeat("./\f", 20_000)) + "src/a.cs";
        var clock = Stopwatch.StartNew();

        AnchorMatcher.Normalized(value).Should().Be("src/a.cs");

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void W2_a_glob_piece_longer_than_any_regex_engine_limit_is_matched_not_thrown()
    {
        var piece = new string('a', 60_000) + "*";

        FluentActions.Invoking(() => AnchorMatcher.Best(ModuleAnchorMatch.Glob, piece, ["src/a.cs"])).Should().NotThrow();
        AnchorMatcher.Best(ModuleAnchorMatch.Glob, "src/" + new string('b', 400) + "*", ["src/" + new string('b', 400) + ".cs"]).Quality.Should().Be(AnchorMatcher.Globbed);
    }

    [Theory]
    [InlineData(ModuleAnchorMatch.Glob)]
    [InlineData(ModuleAnchorMatch.Prefix)]
    [InlineData(ModuleAnchorMatch.Name)]
    public void W1_one_value_of_many_pieces_against_many_anchors_costs_at_most_the_work_bound(ModuleAnchorMatch match)
    {
        // 16,000 distinct pieces in a 64 KiB line, 32 anchors of 512 characters: a million comparisons a record, seconds a walk.
        var value = string.Join(",", Enumerable.Range(0, 16_000).Select(i => $"a{i}*"));
        string[] anchors = [.. Enumerable.Range(0, 32).Select(i => "src/" + new string((char)('a' + i % 26), 508))];
        var query = new ModuleAnchorQuery { ModuleId = "m", Type = "T", Property = "p", Match = match, Values = anchors };
        var matcher = query.Matcher();
        var clock = Stopwatch.StartNew();

        for (var record = 0; record < 20; record++) matcher(value);

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "twenty such records are bounded by the work cap");
        matcher(value).Should().Be(matcher(value), "a cut by work is the same cut every time, in every store");
    }

    [Fact]
    public void The_prepared_matcher_ranks_as_the_one_measure_does()
    {
        string[] values = ["src/a.cs", " ./SRC/b.cs", "lib/**/*.cs", "Neo4jModuleStore"];
        foreach (var match in Enum.GetValues<ModuleAnchorMatch>())
        {
            var matcher = new ModuleAnchorQuery { ModuleId = "m", Type = "T", Property = "p", Match = match, Values = values }.Matcher();
            foreach (var stored in new[] { "src/a.cs", "src/", "lib/x/y.cs", "SRC/B.CS", "**/*.cs", "x Storage/Neo4jModuleStore#", "", " " })
                matcher(stored).Should().Be(ModuleAnchorQuery.Quality(match, stored, values), $"{match} on '{stored}'");
        }
    }
}
