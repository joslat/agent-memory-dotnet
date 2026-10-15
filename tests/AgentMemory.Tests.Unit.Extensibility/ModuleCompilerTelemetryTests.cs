using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleCompilerTelemetryTests
{
    private const string Id = "com.example.notes:notes";

    [Fact]
    public async Task BoundCompileContributionAndDatabaseSpansCarryOnlyFrozenMetadata()
    {
        using var trace = new ModuleTraceCapture();
        var contributor = new Contributor(Id);
        contributor.Result = (_, _) =>
        {
            using var database = AgentMemoryDiagnostics.Source.StartActivity("memory.db.tx");
            return Task.FromResult(new ContextContributionResult { Section = Section(Id) });
        };
        var resolution = Resolution(); var compiler = Bound(resolution, contributor);
        contributor.Descriptor = new("com.example.other:forged", new HashSet<string> { "forged" });
        var result = await compiler.CompileAsync(Request());
        result.Sections.Should().ContainSingle(); contributor.DescriptorReads.Should().Be(1);
        var compile = trace.Stopped.Single(a => a.OperationName == "memory.module.compile");
        var contribution = trace.Stopped.Single(a => a.OperationName == "memory.module.contribute");
        compile.ParentSpanId.Should().Be(trace.ParentSpanId);
        compile.GetTagItem("memory.module.fingerprint").Should().Be(resolution.ManifestFingerprint);
        compile.GetTagItem("memory.module.fingerprint.kind").Should().Be("metadata");
        compile.GetTagItem("memory.module.count").Should().Be(1);
        contribution.ParentSpanId.Should().Be(compile.SpanId);
        contribution.GetTagItem("memory.module.id").Should().Be("com.example.notes");
        contribution.GetTagItem("memory.module.version").Should().Be("1.2.3");
        contribution.GetTagItem("memory.contributor.id").Should().Be(Id);
        contribution.GetTagItem("memory.module.outcome").Should().Be("Section");
        contribution.GetTagItem("memory.module.items").Should().Be(1);
        trace.Stopped.Single(a => a.OperationName == "memory.db.tx").ParentSpanId.Should().Be(contribution.SpanId);
        trace.Stopped.Should().OnlyContain(a => ReferenceEquals(a.Source, AgentMemoryDiagnostics.Source));
        TelemetryText(trace).Should().NotContain("private-").And.NotContain("forged");
    }

    [Theory]
    [InlineData("NotApplicable")]
    [InlineData("SearchedAndEmpty")]
    [InlineData("Unavailable")]
    public async Task TypedOmissionsAreEventsWithoutPrivateDetails(string reason)
    {
        using var trace = new ModuleTraceCapture();
        var contributor = new Contributor(Id) { Applies = reason != "NotApplicable" };
        contributor.Result = (_, _) => reason == "Unavailable" ? throw new InvalidOperationException("private-error")
            : Task.FromResult(new ContextContributionResult { OmissionReason = ContextOmissionReason.SearchedAndEmpty, Detail = "private-detail" });
        var result = await Bound(Resolution(), contributor).CompileAsync(Request());
        result.Omissions.Should().ContainSingle().Which.Reason.ToString().Should().Be(reason);
        var compile = trace.Stopped.Single(a => a.OperationName == "memory.module.compile");
        var contribution = trace.Stopped.Single(a => a.OperationName == "memory.module.contribute");
        contribution.GetTagItem("memory.module.outcome").Should().Be(reason);
        contribution.GetTagItem("memory.module.items").Should().Be(0);
        var omission = compile.Events.Should().ContainSingle().Subject;
        omission.Name.Should().Be("memory.module.omission");
        omission.Tags.Should().Contain(t => t.Key == "memory.contributor.id" && Equals(t.Value, Id));
        omission.Tags.Should().Contain(t => t.Key == "memory.module.outcome" && Equals(t.Value, reason));
        TelemetryText(trace).Should().NotContain("private-");
    }

    [Fact]
    public async Task CancellationClosesBothSpansAndRemainsCancellation()
    {
        using var trace = new ModuleTraceCapture(); using var stop = new CancellationTokenSource();
        var contributor = new Contributor(Id) { Result = (_, _) => { stop.Cancel(); throw new OperationCanceledException("private-cancel", stop.Token); } };
        await ((Func<Task>)(() => Bound(Resolution(), contributor).CompileAsync(Request(), cancellationToken: stop.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
        trace.Stopped.Should().HaveCount(2).And.OnlyContain(a => Equals(a.GetTagItem("memory.module.outcome"), "Cancelled"));
        TelemetryText(trace).Should().NotContain("private-");
    }

    [Fact]
    public async Task ExcludedEmptyCoreOnlyAndLegacyPathsDoNotEmitModuleSpans()
    {
        using var trace = new ModuleTraceCapture();
        var contributor = new Contributor(Id); var compiler = Bound(Resolution(), contributor);
        var excluded = await compiler.CompileAsync(Request(), new HashSet<string> { Id });
        excluded.Sections.Should().BeEmpty(); excluded.Omissions.Should().BeEmpty(); contributor.AppliesCalls.Should().Be(0);
        await Bound(Resolution()).CompileAsync(Request());
        var assembler = Substitute.For<IMemoryContextAssembler>();
        assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "s", AssembledAtUtc = DateTimeOffset.UnixEpoch });
        await Bound(Resolution(), new CoreMemoryContextContributor(assembler)).CompileAsync(Request());
        await new ContextCompiler([contributor], Policy(), TimeProvider.System, NullLogger<ContextCompiler>.Instance).CompileAsync(Request());
        trace.Stopped.Should().BeEmpty();
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("forged-core")]
    public void InvalidBindingsFailBeforeApplicability(string kind)
    {
        var contributor = new Contributor(kind == "foreign" ? "com.example.other:notes" : kind == "forged-core" ? "core.memory" : Id);
        var contributors = kind == "duplicate" ? new[] { contributor, contributor } : new[] { contributor };
        Action bind = () => Bound(Resolution(), contributors);
        bind.Should().Throw<ArgumentException>(); contributor.AppliesCalls.Should().Be(0);
    }

    [Fact]
    public async Task SamplingNothingDoesNotChangeContribution()
    {
        using var trace = new ModuleTraceCapture(ActivitySamplingResult.None);
        var result = await Bound(Resolution(), new Contributor(Id)).CompileAsync(Request());
        result.Sections.Should().ContainSingle(); trace.Stopped.Should().BeEmpty();
    }

    [Fact]
    public async Task LegacyRegistrationDoesNotConsumeAmbientModuleResolution()
    {
        using var trace = new ModuleTraceCapture();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Policy());
        services.AddSingleton(Resolution()); services.AddScoped<IContextContributor>(_ => new Contributor("legacy"));
        services.AddAgentMemoryExtensibility();
        using var provider = services.BuildServiceProvider(validateScopes: true); using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IContextCompiler>().CompileAsync(Request());
        result.Sections.Should().ContainSingle(); trace.Stopped.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsampledOrLegacyCompilationDoesNotCountItemsForTelemetry(bool bound)
    {
        using var trace = new ModuleTraceCapture(ActivitySamplingResult.None);
        var items = new CountingItems();
        var contributor = new Contributor(Id) { Result = (_, _) => Task.FromResult(new ContextContributionResult
            { Section = new ContextSection(Id + ":section", Id, 1, items) }) };
        var compiler = bound ? Bound(Resolution(), contributor)
            : new ContextCompiler([contributor], Policy(), TimeProvider.System, NullLogger<ContextCompiler>.Instance);
        (await compiler.CompileAsync(Request())).Sections.Should().ContainSingle();
        items.CountReads.Should().Be(0, "telemetry must not inspect item counts when no data is sampled");
    }

    [Fact]
    public async Task ExplicitRegistrationUsesCapturedResolutionAndScopedContributors()
    {
        using var trace = new ModuleTraceCapture();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(Policy());
        var resolution = Resolution();
        services.AddScoped<IContextContributor>(_ => new Contributor(Id));
        services.AddAgentMemoryExtensibility();
        services.AddAgentMemoryExtensibility(resolution);
        services.AddAgentMemoryExtensibility(); // Repeated legacy registration preserves the explicit selection.
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var compiler = first.ServiceProvider.GetRequiredService<IContextCompiler>();
        compiler.Should().BeSameAs(first.ServiceProvider.GetRequiredService<IContextCompiler>());
        compiler.Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<IContextCompiler>());
        (await compiler.CompileAsync(Request())).Sections.Should().ContainSingle();
        trace.Stopped.Single(a => a.OperationName == "memory.module.compile")
            .GetTagItem("memory.module.fingerprint").Should().Be(resolution.ManifestFingerprint);
        provider.GetService<ModuleResolution>().Should().BeNull("explicit binding must not install an ambient catalog");
    }

    [Fact]
    public void FailedResolutionRefusesRegistrationWithoutChangingServices()
    {
        var resolution = ModuleResolver.Resolve(Resolution().Modules, ModuleVersion.Parse("9.0.0"), new HashSet<string>());
        resolution.Succeeded.Should().BeFalse();
        var services = new ServiceCollection();
        Action register = () => services.AddAgentMemoryExtensibility(resolution);
        register.Should().Throw<ArgumentException>(); services.Should().BeEmpty();
    }

    [Fact]
    public async Task CompilationCountsDistinctEligibleModulesAndPreservesExclusions()
    {
        using var trace = new ModuleTraceCapture();
        var notes = new Contributor(Id); var secondNotes = new Contributor("com.example.notes:second");
        var other = new Contributor("com.example.other:notes");
        var resolution = ModuleResolver.Resolve(Resolution().Modules.Concat([
            ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.other","version":"2.0.0","sdk":"[0.1,0.2)"}""")
        ]).ToArray(), ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var compiler = Bound(resolution, notes, secondNotes, other);
        var result = await compiler.CompileAsync(Request(), new HashSet<string> { other.Descriptor.Id });
        result.Sections.Should().HaveCount(2); result.Omissions.Should().BeEmpty(); other.AppliesCalls.Should().Be(0);
        var compile = trace.Stopped.Single(a => a.OperationName == "memory.module.compile");
        compile.GetTagItem("memory.module.count").Should().Be(1);
        compile.GetTagItem("memory.module.items").Should().Be(2);
        trace.Stopped.Where(a => a.OperationName == "memory.module.contribute").Should().HaveCount(2)
            .And.OnlyContain(a => Equals(a.GetTagItem("memory.module.id"), "com.example.notes"));
    }

    private sealed class CountingItems : IReadOnlyList<ContextItem>
    {
        public int CountReads { get; private set; }
        public int Count { get { CountReads++; return 1; } }
        public ContextItem this[int index] => index == 0 ? new("record", "text") : throw new IndexOutOfRangeException();
        public IEnumerator<ContextItem> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static string TelemetryText(ModuleTraceCapture trace) => string.Join("|", trace.Stopped.SelectMany(a =>
        a.TagObjects.Select(t => t.Value?.ToString()).Concat(a.Events.SelectMany(e => e.Tags.Select(t => t.Value?.ToString())))));
    private static ContextRequest Request() => new() { SessionId = "private-session", Owner = "private-owner", Query = "private-query" };
    private static ContextSection Section(string id) => new(id + ":section", id, 1, [new("private-record", "private-text")]);
    private static ContextCompiler Bound(ModuleResolution resolution, params IContextContributor[] contributors) =>
        new(resolution, contributors, Policy(), TimeProvider.System, NullLogger<ContextCompiler>.Instance);
    private static IMemoryIsolationPolicy Policy()
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.For("private-resolved-owner"));
        return policy;
    }
    private static ModuleResolution Resolution() => ModuleResolver.Resolve([
        ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.notes","version":"1.2.3","sdk":"[0.1,0.2)"}""")
    ], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
    private sealed class Contributor(string id) : IContextContributor
    {
        private ContextContributorDescriptor _descriptor = new(id, new HashSet<string> { id + ":section" });
        public int DescriptorReads { get; private set; }
        public ContextContributorDescriptor Descriptor { get { DescriptorReads++; return _descriptor; } set => _descriptor = value; }
        public bool Applies { get; init; } = true;
        public int AppliesCalls { get; private set; }
        public Func<ContextRequest, CancellationToken, Task<ContextContributionResult>> Result { get; set; } = (_, _) => Task.FromResult(new ContextContributionResult { Section = Section(id) });
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) { AppliesCalls++; return ValueTask.FromResult(Applies); }
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Use the typed result path");
        public Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default) => Result(request, cancellationToken);
    }
}
