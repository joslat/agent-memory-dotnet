using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// CI guard (task 4.8) against documentation drift and dependency-rule violations in
/// <c>AgentMemory.Abstractions</c>. If these fail after an intentional change, update BOTH the
/// constant here AND the catalogs in <c>docs/design.md §5/§6</c> and the counts in
/// <c>docs/architecture.md §3.1</c> so code and docs stay in lockstep.
/// </summary>
public sealed class AbstractionsContractGuardTests
{
    private static readonly Assembly Abstractions = typeof(IMemoryService).Assembly;

    // Counts mirrored in docs/architecture.md §3.1 and docs/design.md §5/§6.
    private const int DocumentedServiceInterfaces = 41; // +IMultiSessionUnifiedMemoryExtractor (M-27-V2 LAB-B1)
    private const int DocumentedRepositoryInterfaces = 11;
    private const int DocumentedDomainRecords = 51; // +UnifiedExtractionResult (M-27-V2 LAB-U1)
    private const int DocumentedEnums = 24; // +MemoryProfile, +RankingIntent, +DuplicateStatus, +EntityMatchType, +MemoryNodeKind, +MemoryOperationAccess, +MemoryIsolationMode (#100); +IngestionStatus, +IngestionStage, +IngestionItemStatus, +MemoryItemKind, +IngestionFailureMode (#101); +MemoryTrustLevel (#92 Phase 3)

    private static IEnumerable<Type> PublicTypes() =>
        Abstractions.GetTypes().Where(t => t.IsPublic);

    [Fact]
    public void ServiceInterfaceCount_MatchesDocumentedCatalog()
    {
        var count = PublicTypes().Count(t =>
            t.IsInterface && t.Namespace == "AgentMemory.Abstractions.Services");

        count.Should().Be(DocumentedServiceInterfaces,
            "docs/design.md §5 and architecture.md §3.1 claim {0} service interfaces", DocumentedServiceInterfaces);
    }

    [Fact]
    public void RepositoryInterfaceCount_MatchesDocumentedCatalog()
    {
        var count = PublicTypes().Count(t =>
            t.IsInterface && t.Namespace == "AgentMemory.Abstractions.Repositories");

        count.Should().Be(DocumentedRepositoryInterfaces,
            "docs/design.md §6 claims {0} repository interfaces", DocumentedRepositoryInterfaces);
    }

    [Fact]
    public void EnumCount_MatchesDocumentedCount()
    {
        var count = PublicTypes().Count(t =>
            t.IsEnum && (t.Namespace?.StartsWith("AgentMemory.Abstractions", StringComparison.Ordinal) ?? false));

        count.Should().Be(DocumentedEnums,
            "docs/architecture.md §3.1 claims {0} enums", DocumentedEnums);
    }

    [Fact]
    public void DomainRecordCount_MatchesDocumentedCount()
    {
        var count = PublicTypes().Count(t =>
            (t.Namespace?.StartsWith("AgentMemory.Abstractions.Domain", StringComparison.Ordinal) ?? false)
            && IsRecord(t));

        count.Should().Be(DocumentedDomainRecords,
            "docs/architecture.md §3.1 claims {0} domain records", DocumentedDomainRecords);
    }

    [Fact]
    public void Abstractions_CompiledReferences_ExcludeForbiddenDependencies()
    {
        // Defense-in-depth: whatever the assembly actually links must not include forbidden deps.
        var referenced = Abstractions.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(n => n.StartsWith("AgentMemory.", StringComparison.Ordinal),
            "AgentMemory.Abstractions must not depend on any sibling AgentMemory.* package");
        referenced.Should().NotContain("Neo4j.Driver");
        referenced.Should().NotContain(n => n.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal));
        referenced.Should().NotContain(n => n.StartsWith("Microsoft.Agents", StringComparison.Ordinal));
        // Never the full Microsoft.Extensions.AI implementation package.
        referenced.Should().NotContain("Microsoft.Extensions.AI");
    }

    [Fact]
    public void Abstractions_Csproj_DeclaresOnlyMicrosoftExtensionsAiAbstractions_AndNoProjectReferences()
    {
        var csprojPath = Path.Combine(RepoRoot(), "src", "AgentMemory.Abstractions", "AgentMemory.Abstractions.csproj");
        File.Exists(csprojPath).Should().BeTrue("the Abstractions project file must exist at {0}", csprojPath);

        var doc = XDocument.Load(csprojPath);

        var packageRefs = doc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        packageRefs.Should().ContainSingle()
            .Which.Should().Be("Microsoft.Extensions.AI.Abstractions",
                "Abstractions may reference only Microsoft.Extensions.AI.Abstractions");

        var projectRefs = doc.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .ToList();
        projectRefs.Should().BeEmpty("Abstractions is the contract root and must not reference other projects");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentMemory.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run within the repository (AgentMemory.slnx not found above {0})", AppContext.BaseDirectory);
        return dir!.FullName;
    }

    /// <summary>Records (class) carry a compiler-generated <c>&lt;Clone&gt;$</c> method.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Any(m => m.Name == "<Clone>$");
}
