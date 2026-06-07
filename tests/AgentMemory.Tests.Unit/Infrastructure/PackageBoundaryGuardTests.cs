using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// CI guard for the package boundary rules in <c>docs/architecture.md §5</c> (B2–B6, B8) for the
/// lower layers. B1 (Abstractions) is covered by <see cref="AbstractionsContractGuardTests"/>; B7
/// ("no business logic in adapters") is a non-mechanical review rule.
///
/// Each rule is checked two ways: the assembly's actually-linked references (defense-in-depth, catches
/// transitive leaks) and the .csproj's declared Package/Project references (catches declared-but-unused).
/// If these fail after an intentional architecture change, update BOTH the rule here AND architecture.md §5.
/// </summary>
public sealed class PackageBoundaryGuardTests
{
    private sealed record BoundaryRule(
        string Project,
        Assembly Assembly,
        string[] ForbiddenExternalPrefixes,
        string[] AllowedInternalReferences);

    // Forbidden framework-adapter SDK families (apply to both Core and Neo4j).
    private static readonly string[] FrameworkSdkPrefixes =
        ["Microsoft.Agents", "Microsoft.SemanticKernel", "ModelContextProtocol"];

    private static readonly Dictionary<string, BoundaryRule> RuleTable = new[]
    {
        // B2 (no Neo4j.Driver) + B3/B4 (no framework SDKs) + B8 (only Abstractions among siblings).
        new BoundaryRule(
            "AgentMemory.Core",
            typeof(MemoryContextAssembler).Assembly,
            ForbiddenExternalPrefixes: [.. FrameworkSdkPrefixes, "Neo4j.Driver"],
            AllowedInternalReferences: ["AgentMemory.Abstractions"]),

        // B5/B6 (no framework SDKs) + B8 (only Abstractions + Core among siblings); Neo4j.Driver is allowed here.
        new BoundaryRule(
            "AgentMemory.Neo4j",
            typeof(Neo4jFactRepository).Assembly,
            ForbiddenExternalPrefixes: FrameworkSdkPrefixes,
            AllowedInternalReferences: ["AgentMemory.Abstractions", "AgentMemory.Core"]),
    }.ToDictionary(r => r.Project);

    public static IEnumerable<object[]> ProjectNames() =>
        RuleTable.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void CompiledReferences_HonorBoundary(string project)
    {
        var rule = RuleTable[project];
        var referenced = rule.Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        foreach (var prefix in rule.ForbiddenExternalPrefixes)
            referenced.Should().NotContain(n => n.StartsWith(prefix, StringComparison.Ordinal),
                "{0} must not reference {1}* (architecture.md §5)", project, prefix);

        var forbiddenSiblings = referenced.Where(n =>
            n.StartsWith("AgentMemory.", StringComparison.Ordinal)
            && !rule.AllowedInternalReferences.Contains(n)).ToList();
        forbiddenSiblings.Should().BeEmpty(
            "{0} may only reference these sibling packages: {1}", project,
            string.Join(", ", rule.AllowedInternalReferences));
    }

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Csproj_HonorsBoundary(string project)
    {
        var rule = RuleTable[project];
        var csprojPath = Path.Combine(RepoRoot(), "src", project, $"{project}.csproj");
        File.Exists(csprojPath).Should().BeTrue("the project file must exist at {0}", csprojPath);
        var doc = XDocument.Load(csprojPath);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();
        foreach (var prefix in rule.ForbiddenExternalPrefixes)
            packageRefs.Should().NotContain(p => p.StartsWith(prefix, StringComparison.Ordinal),
                "{0}.csproj must not declare a {1}* PackageReference", project, prefix);

        var projectRefNames = doc.Descendants("ProjectReference")
            // Normalize Windows '\' separators to '/' before extracting the file name: csproj Include
            // paths use backslashes, but '\' is not a path separator on Linux/CI, so
            // Path.GetFileNameWithoutExtension would otherwise return the whole "..\X\X" path there.
            .Select(e => Path.GetFileNameWithoutExtension((e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        projectRefNames.Should().OnlyContain(n => rule.AllowedInternalReferences.Contains(n),
            "{0}.csproj may only ProjectReference: {1}", project,
            string.Join(", ", rule.AllowedInternalReferences));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run within the repository (AgentMemory.slnx not found above {0})", AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
