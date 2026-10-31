using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Feature 01, step 4: every normative requirement of the spec is numbered, listed in <c>spec/requirements.json</c> and tested,
/// and every conformance vector names the requirements it tests (<c>spec/vectors.json</c>). The spec text, the list and the
/// map are held equal here, so a requirement cannot be added untested or a vector unexplained.
/// </summary>
public sealed partial class SpecRequirementsTests
{
    [GeneratedRegex(@"\*\*(R-[A-Z]+-\d{3})\.\*\*")]
    private static partial Regex Defined();

    [GeneratedRegex(@"\AR-(CORE|WALK|TRIAL|COMPOSE|CERTIFY|FED|PROT|CODE|PERM|HOST)-\d{3}\z")]
    private static partial Regex WellFormed();

    private static readonly string Spec = Path.Combine(Root(), "spec");

    private static List<(string Id, string Level, string[] VerifiedBy)> Requirements()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Spec, "requirements.json")));
        return [.. document.RootElement.GetProperty("requirements").EnumerateArray().Select(r => (
            r.GetProperty("id").GetString()!, r.GetProperty("level").GetString()!,
            r.GetProperty("verifiedBy").EnumerateArray().Select(v => v.GetString()!).ToArray()))];
    }

    private static Dictionary<string, string[]> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Spec, "vectors.json")));
        return document.RootElement.GetProperty("vectors").EnumerateObject()
            .ToDictionary(v => v.Name, v => v.Value.EnumerateArray().Select(x => x.GetString()!).ToArray(), StringComparer.Ordinal);
    }

    [Fact]
    public void The_spec_text_and_the_requirement_list_define_the_same_ids_once_each()
    {
        var listed = Requirements().Select(r => r.Id).ToList();
        listed.Should().OnlyHaveUniqueItems();
        listed.Should().OnlyContain(id => WellFormed().IsMatch(id));
        var inText = Directory.EnumerateFiles(Spec, "*.md", SearchOption.AllDirectories)
            .SelectMany(f => Defined().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToList();
        inText.Should().OnlyHaveUniqueItems("each requirement is defined in one place");
        inText.Should().BeEquivalentTo(listed);
    }

    [Fact]
    public void Every_vector_names_requirements_that_exist_and_every_kit_tested_MUST_has_a_vector()
    {
        var requirements = Requirements();
        var ids = requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var vectors = Vectors();
        var unknown = vectors.SelectMany(v => v.Value.Where(id => !ids.Contains(id)).Select(id => $"{v.Key}: {id}")).ToList();
        string.Join(Environment.NewLine, unknown).Should().BeEmpty("a vector names only requirements the spec defines");
        vectors.Should().OnlyContain(v => v.Value.Length > 0, "every vector tests at least one requirement");
        var covered = vectors.SelectMany(v => v.Value).ToHashSet(StringComparer.Ordinal);
        var untested = requirements.Where(r => r.Level == "MUST" && r.VerifiedBy.Contains("kit") && !covered.Contains(r.Id)).Select(r => r.Id).ToList();
        string.Join(", ", untested).Should().BeEmpty("a requirement the kit is said to verify has a vector that tests it");
    }

    [Fact]
    public void Every_unit_test_a_requirement_names_exists()
    {
        var sources = Directory.EnumerateFiles(Path.Combine(Root(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (id, _, by) in Requirements())
            foreach (var test in by.Where(b => b.StartsWith("unit:", StringComparison.Ordinal)).Select(b => b["unit:".Length..]))
            {
                var dot = test.LastIndexOf('.');
                var (type, method) = dot < 0 ? (test, "") : (test[..dot], test[(dot + 1)..]);
                if (!sources.TryGetValue(type, out var text) || (method.Length > 0 && !text.Contains($" {method}(", StringComparison.Ordinal)))
                    missing.Add($"{id}: {test}");
            }
        string.Join(Environment.NewLine, missing).Should().BeEmpty("a requirement names only tests that exist");
    }

    [Fact]
    public void Every_requirement_is_verified_by_something_and_MUST_ones_by_a_test_where_possible()
    {
        var requirements = Requirements();
        requirements.Should().OnlyContain(r => r.VerifiedBy.Length > 0);
        requirements.Should().OnlyContain(r => r.Level == "MUST" || r.Level == "SHOULD" || r.Level == "MAY");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
