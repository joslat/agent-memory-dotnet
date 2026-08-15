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
    private const int DocumentedServiceInterfaces = 46; // +IMemoryAccessTracker (30.12 — Track returns VOID by contract, not oversight: a Task-returning version invites an await, which reinstates exactly the latency the queue exists to remove) // +IEntitySummarySynthesizer, +IEntitySummaryService (PLAN 12.7 / S1)
    private const int DocumentedRepositoryInterfaces = 12; // +IEntitySummaryRepository (PLAN 12.7 / S1)
    private const int DocumentedDomainRecords = 72; // +ForgottenTopicSummary (30.8 — a summary, never the forgotten facts: rendering the content would undo the forgetting, putting the decayed values back in the prompt to be answered from); +ProspectiveDueResult (30.7 — two lists rather than one, because "just became true" and "about to stop being true" are different claims and a reader who has to infer which from the dates ignores the block); +MemoryDelta, +SupersededFactPair, +SupersededPreferencePair, +MemoryDeltaRequest, +FactDeltaRows, +PreferenceDeltaRows (30.5 delta recall — the two *DeltaRows records are the repository-boundary shape, kept distinct from MemoryDelta so a repository cannot silently return a partially-assembled delta); +EntitySummary (PLAN 12.7 / S1; EntitySummarySource is a record STRUCT and the guard counts reference records only); +MemoryBlock, +MemoryBlockLine (PLAN 12.9 / S4); +BulkIngestionOptions, +BulkIngestionOutcome, +BulkIngestionResult (PLAN 2.9 / rank 27); +ProjectedContext, +ProjectedItemAnnotation, +ProjectedBlock (30.2 projection layer — MemoryProjectionOptions lives in .Options and is not counted here); +SupersededFact (30.2 step 6: what a fact USED to say, deliberately not a whole Fact — a predecessor is read only to render "since D; previously Y", and returning full facts would carry embeddings across a boundary with no use for them)
    private const int DocumentedEnums = 32; // +TraceKind (7.1), +ExtractionProvenanceMode (9.3), +TemporalQueryClocks (13.2), +ProjectedBlockKind (30.2), +DerivationOperators (30.6 — a [Flags] enum rather than a bool per operator, so a host selects arithmetic as a set and the reachability guard can assert every flag has exactly one evaluator)

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
