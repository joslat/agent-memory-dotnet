using System.Text.Json;
using AgentMemory.Packs.Format;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Feature 01: <c>spec/registry.json</c> is the one definition of every list of names the format defines. The readers derive
/// their sets from it, and no copy of a list is written by hand anywhere else (the twin-divergence root of rounds 7 to 14:
/// the field list lived in .NET, in Python and in the spec).
/// </summary>
public sealed class SpecRegistryTests
{
    // Places a list may legitimately appear outside the registry, each with its reason.
    private static readonly (string File, string Why)[] Allowed =
    [
        ("conformance/pack-v1.1/run_vectors.py", "test data: trust policies the kit builds on purpose"),
        ("demobrain/src/DemoBrain.App/PackComposerWindow.Build.cs", "a role picker in a private demo app; noted for feature 01 step 2"),
        ("tools/Ampack/Program.cs", "the revoke command's option names, which mirror the revocation fields on purpose; no document is read with them"),
    ];

    [Fact]
    public void The_readers_take_every_list_from_the_registry()
    {
        using var registry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root(), "spec", "registry.json")));
        var r = registry.RootElement;
        static string[] List(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
        PackManifest.Fields.Should().BeEquivalentTo(List(r.GetProperty("manifest").GetProperty("fields")));
        PackManifest.Kinds.Should().BeEquivalentTo(List(r.GetProperty("manifest").GetProperty("values").GetProperty("kind")));
        PackManifest.Tiers.Should().BeEquivalentTo(List(r.GetProperty("manifest").GetProperty("values").GetProperty("tier")));
        Dsse.Roles.Should().BeEquivalentTo(List(r.GetProperty("roles").GetProperty("signing")));
        TrustPolicy.KeyRoles.Should().BeEquivalentTo(List(r.GetProperty("roles").GetProperty("keys")));
        PackCertificate.Targets.Should().BeEquivalentTo(List(r.GetProperty("trial").GetProperty("targets")));
    }

    [Fact]
    public void No_list_of_the_registry_is_written_by_hand_elsewhere()
    {
        // Any list of three or more names, written as a literal sequence of its first four (all, when it has three) in a source
        // file, is a hand copy. Four, not three: every document of the format begins with format, id and version.
        var root = Root();
        using var registry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "spec", "registry.json")));
        var lists = new List<string[]>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3 && e.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String))
                lists.Add([.. e.EnumerateArray().Select(x => x.GetString()!)]);
            else if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) Walk(p.Value);
        }
        Walk(registry.RootElement);
        var sources = new[] { "src", "tools", "shared", "conformance", "demobrain/src" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".py", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var found = new List<string>();
        foreach (var file in sources)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (Allowed.Any(a => a.File == relative)) continue;
            var text = File.ReadAllText(file);
            // The same list written as a pattern: n is not ("a" or "b" or "c").
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"is not \((""[^""]+""(?:\s+or\s+""[^""]+""){2,})\)"))
            {
                var names = System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"""([^""]+)""").Select(n => n.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
                if (lists.Any(l => l.ToHashSet(StringComparer.Ordinal).SetEquals(names)))
                    found.Add($"{relative}: a hand copy of [{string.Join(", ", names)}] written as a pattern");
            }
            foreach (var list in lists)
                if (text.Contains(string.Join(", ", list.Take(4).Select(n => $"\"{n}\"")), StringComparison.Ordinal))
                    found.Add($"{relative}: a hand copy of [{string.Join(", ", list.Take(4))}, …]");
        }
        string.Join(Environment.NewLine, found).Should().BeEmpty("every list comes from spec/registry.json");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root holds AgentMemory.slnx.");
    }
}
