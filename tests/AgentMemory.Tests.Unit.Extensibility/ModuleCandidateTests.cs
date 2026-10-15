using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleCandidateTests
{
    private const string Module = "com.example.candidates";
    private const string ContributorId = Module + ":recent";
    private const string SectionType = Module + ":items";

    [Theory]
    [InlineData("foreign-contributor")]
    [InlineData("undeclared-section")]
    public async Task BoundOutputCannotRelabelItsContributorOrUndeclaredSection(string kind)
    {
        var section = Section() with
        {
            ContributorId = kind == "foreign-contributor" ? "com.foreign.notes:recent" : ContributorId,
            TypeId = kind == "undeclared-section" ? "com.foreign.notes:items" : SectionType
        };
        var result = await Compiler(section).CompileAsync(new() { SessionId = "fresh", Owner = "owner" });
        result.Sections.Should().BeEmpty();
        result.Omissions.Single().Reason.Should().Be(ContextOmissionReason.Unavailable);
    }

    [Fact]
    public async Task BoundOutputCarriesHostResolvedIdentityWithoutReformattingItems()
    {
        var section = Section();
        var output = (await Compiler(section).CompileAsync(new() { SessionId = "fresh", Owner = "owner" })).Sections.Single();
        var property = typeof(ContextSection).GetProperty("Binding");
        property.Should().NotBeNull("host provenance must survive compilation for later selection");
        var binding = property!.GetValue(output);
        binding.Should().NotBeNull();
        binding!.GetType().GetProperty("ModuleId")!.GetValue(binding).Should().Be(Module);
        binding.GetType().GetProperty("ModuleVersion")!.GetValue(binding).Should().Be("1.2.3");
        binding.GetType().GetProperty("MetadataFingerprint")!.GetValue(binding).Should().Be(Resolution().ManifestFingerprint);
        output.Items.Should().BeSameAs(section.Items);
        output.Metadata.Should().BeSameAs(section.Metadata);
    }

    [Fact]
    public async Task LegacyCompilationDoesNotReuseAnotherCompilersMetadataBinding()
    {
        var bound = (await Compiler(Section()).CompileAsync(new() { SessionId = "first" })).Sections.Single();
        var legacy = await Compiler(bound, attachBinding: false).CompileAsync(new() { SessionId = "second" });
        legacy.Sections.Single().Binding.Should().BeNull();
        legacy.Sections.Single().Items.Should().BeSameAs(bound.Items);
        typeof(ContextSection).GetProperty("Binding")!.SetMethod!.IsAssembly.Should().BeTrue();
    }

    [Fact]
    public async Task ProjectionKeepsVersionSourcesAndUsesIdentityInsteadOfText()
    {
        var sources = new List<string> { "source-one", "source-two" };
        var provenance = new ContextItemProvenance
        {
            RecordModuleId = Module, RecordType = "CandidateItem", RecordVersionId = "record-v2", RecordKey = "goal",
            CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "teaching", SourceMessageIds = sources
        };
        var section = Section() with { Items = [new("record-v2", "private text") { Provenance = provenance }] };
        var bound = (await Compiler(section).CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        var projected = ModuleContextCandidates.Project(bound, 10);
        var candidate = projected.Candidates.Single();
        sources.Clear();
        candidate.Provenance!.SourceMessageIds.Should().Equal("source-one", "source-two");
        candidate.Provenance.RecordKey.Should().Be("goal");
        candidate.Binding.ModuleVersion.Should().Be("1.2.3");
        candidate.Text.Should().Be("private text");
        candidate.Key.Should().StartWith("module:").And.NotContain("private");
        var alteredText = bound with { Items = [new("record-v2", "another rendering") { Provenance = provenance }] };
        ModuleContextCandidates.Project(alteredText, 10).Candidates.Single().Key.Should().Be(candidate.Key);
        var nextVersion = bound with { Items = [new("record-v3", "private text") { Provenance = provenance with { RecordVersionId = "record-v3" } }] };
        ModuleContextCandidates.Project(nextVersion, 10).Candidates.Single().Key.Should().NotBe(candidate.Key);
    }

    [Fact]
    public async Task SameTextAndIdInDifferentModulesNeverShareCandidateIdentity()
    {
        var first = (await Compiler(Section()).CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        const string other = "com.example.other";
        var second = (await Compiler(Section() with { ContributorId = other + ":recent" }, other)
            .CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        ModuleContextCandidates.Project(first, 10).Candidates.Single().Key.Should()
            .NotBe(ModuleContextCandidates.Project(second, 10).Candidates.Single().Key);
    }

    [Fact]
    public async Task ProjectionIsBoundedWithoutEnumeratingOrReadingTheUnselectedTail()
    {
        var bound = (await Compiler(Section()).CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        var items = new BoundedItems();
        var projection = ModuleContextCandidates.Project(bound with { Items = items }, 1);
        projection.OfferedItems.Should().Be(3); projection.ItemLimitOmitted.Should().Be(2);
        projection.Candidates.Should().ContainSingle(); items.IndexReads.Should().Be(1);
        ModuleContextCandidates.Project(bound with { Items = items }, 0).Candidates.Should().BeEmpty();
        items.IndexReads.Should().Be(1);
    }

    [Fact]
    public async Task ProjectionRefusesUnboundRelabeledAndInconsistentRecordIdentities()
    {
        Action unbound = () => ModuleContextCandidates.Project(Section(), 1);
        unbound.Should().Throw<ArgumentException>();
        var bound = (await Compiler(Section()).CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        Action relabeled = () => ModuleContextCandidates.Project(bound with { ContributorId = "foreign" }, 1);
        relabeled.Should().Throw<ArgumentException>();
        Action invalidRecord = () => ModuleContextCandidates.Project(bound with { Items = [new("record-v2", "text")
        {
            Provenance = new() { RecordModuleId = Module, RecordType = "Item", RecordVersionId = "record-v3",
                RecordKey = "key", CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "teaching" }
        }] }, 1);
        invalidRecord.Should().Throw<ArgumentException>();
        Action negativeCap = () => ModuleContextCandidates.Project(bound, -1);
        negativeCap.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task OrdinalOnlyItemsHaveDistinctKeysAndDoNotPretendToBeStoredVersions()
    {
        var bound = (await Compiler(Section() with { Items = [new(null, "same"), new(null, "same")] })
            .CompileAsync(new() { SessionId = "fresh" })).Sections.Single();
        var first = ModuleContextCandidates.Project(bound, 2).Candidates;
        var again = ModuleContextCandidates.Project(bound, 2).Candidates;
        first.Select(c => c.Key).Should().OnlyHaveUniqueItems().And.Equal(again.Select(c => c.Key));
        first.Should().OnlyContain(c => c.ItemId == null && c.Provenance == null);
    }

    private sealed class BoundedItems : IReadOnlyList<ContextItem>
    {
        public int Count => 3;
        public int IndexReads { get; private set; }
        public ContextItem this[int index] => index == 0 ? Read() : throw new InvalidOperationException("Unselected tail was read");
        private ContextItem Read() { IndexReads++; return new("first", "exact text"); }
        public IEnumerator<ContextItem> GetEnumerator() => throw new InvalidOperationException("Do not enumerate the unbounded input");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static ContextSection Section() => new(SectionType, ContributorId, 1, [new("record-v2", "private text")], new object());
    private static ContextCompiler Compiler(ContextSection section, string module = Module, bool attachBinding = true)
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.For("resolved"));
        var contributor = new Contributor(section, module + ":recent");
        return attachBinding
            ? new ContextCompiler(Resolution(module), [contributor], policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance)
            : new ContextCompiler([contributor], policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance);
    }
    private static ModuleResolution Resolution(string module = Module) => ModuleResolver.Resolve([
        ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.candidates","version":"1.2.3","sdk":"[0.1,0.2)"}""".Replace(Module, module, StringComparison.Ordinal))
    ], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
    private sealed class Contributor(ContextSection section, string contributorId) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } = new(contributorId, new HashSet<string> { SectionType });
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) => Task.FromResult<ContextSection?>(section);
    }
}
