using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ViewContributorTests
{
    [Theory]
    [InlineData(ModuleReadStatus.Refused, ContextOmissionReason.Denied)]
    [InlineData(ModuleReadStatus.NotApplicable, ContextOmissionReason.NotApplicable)]
    public async Task CompilerRetainsViewDispositionInsteadOfReportingEmpty(ModuleReadStatus status, ContextOmissionReason reason)
    {
        var reader = new Reader { Result = new() { Status = status, Errors = ["view problem"] } };
        var envelope = await Compiler(reader).CompileAsync(new() { SessionId = "fresh", Owner = "claimed" });
        envelope.Sections.Should().BeEmpty(); envelope.Omissions.Single().Reason.Should().Be(reason);
    }

    [Fact]
    public async Task ContributorUsesSharedRenderingAndPreservesResolvedScopeClockAndRecordIdentity()
    {
        var record = new ModuleRecord { ModuleId = "com.example.views", Type = "ViewsItem", Id = "record-v2", Key = "goal",
            CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "teaching", Source = new() { MessageIds = ["source"], Excerpt = "lesson" },
            Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("lesson") } };
        var reader = new Reader { Result = new() { Status = ModuleReadStatus.Succeeded, Records = [record] } };
        var pin = DateTimeOffset.UnixEpoch.AddDays(1);
        var envelope = await Compiler(reader).CompileAsync(new() { SessionId = "fresh", Owner = "claimed", SystemAsOf = pin });
        var item = envelope.Sections.Single().Items.Single(); item.Id.Should().Be("record-v2"); item.Text.Should().Be("Remember lesson");
        reader.Context!.Scope.Should().Be(MemoryScope.For("resolved", includeShared: false));
        reader.Context.SessionId.Should().Be("fresh"); reader.Context.SystemAsOfUtc.Should().Be(pin);
        ((ModuleViewResult)envelope.Sections.Single().Metadata!).Items.Single().Record.Should().BeSameAs(record);
    }

    [Fact]
    public async Task UnsupportedValidTimeDoesNotCallStorageAndEmptyReadIsDistinct()
    {
        var reader = new Reader(); var compiler = Compiler(reader);
        var historic = await compiler.CompileAsync(new() { SessionId = "s", Owner = "owner", AsOf = DateTimeOffset.UnixEpoch });
        historic.Omissions.Single().Reason.Should().Be(ContextOmissionReason.NotApplicable); reader.Calls.Should().Be(0);
        var current = await compiler.CompileAsync(new() { SessionId = "s", Owner = "owner" });
        current.Omissions.Single().Reason.Should().Be(ContextOmissionReason.SearchedAndEmpty); reader.Calls.Should().Be(1);
    }

    [Fact]
    public async Task RuntimeArgumentSelectorRunsOnceWithResolvedOwnerAndPreservesSharedVisibility()
    {
        var reader = new Reader(); var calls = 0;
        var compiler = Compiler(reader, request =>
        {
            calls++; request.Owner.Should().Be("resolved"); request.Query.Should().Be("selected-key");
            return new() { Key = request.Query, Limit = 1 };
        });
        await compiler.CompileAsync(new() { SessionId = "fresh", Owner = "claimed", Query = "selected-key" });
        calls.Should().Be(1); reader.Query!.Key.Should().Be("selected-key"); reader.Query.Limit.Should().Be(1);
        reader.Context!.Scope.Should().Be(MemoryScope.For("resolved", includeShared: false));
    }

    [Fact]
    public async Task NullSelectionIsNotApplicableAndInvalidArgumentsAreDeniedWithoutStorage()
    {
        var reader = new Reader();
        var skipped = await Compiler(reader, _ => null).CompileAsync(new() { SessionId = "fresh", Owner = "claimed" });
        skipped.Omissions.Single().Reason.Should().Be(ContextOmissionReason.NotApplicable);
        var denied = await Compiler(reader, _ => new() { Limit = 4 }).CompileAsync(new() { SessionId = "fresh", Owner = "claimed" });
        denied.Omissions.Single().Reason.Should().Be(ContextOmissionReason.Denied);
        reader.Calls.Should().Be(0);
    }

    [Fact]
    public async Task UnsupportedClockNeverInvokesTheArgumentSelector()
    {
        var reader = new Reader(); var calls = 0;
        await Compiler(reader, _ => { calls++; return new(); }).CompileAsync(new() { SessionId = "fresh", AsOf = DateTimeOffset.UnixEpoch });
        calls.Should().Be(0); reader.Calls.Should().Be(0);
    }

    private static ContextCompiler Compiler(Reader reader, Func<ContextRequest, ModuleViewArguments?>? arguments = null)
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(null, Arg.Any<string?>(), Arg.Any<string>(), AgentMemory.Abstractions.Domain.MemoryOperationAccess.Tenant)
            .Returns(MemoryScope.For("resolved", includeShared: false));
        var contributor = arguments is null ? new ViewContributor(Resolution(), reader, "all") : new ViewContributor(Resolution(), reader, "all", 100, arguments);
        return new ContextCompiler([contributor], policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance);
    }
    private static ModuleResolution Resolution() => ModuleResolver.Resolve([ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.views","version":"1.0.0","sdk":"[0.1,0.2)","ontology":{"labelPrefix":"Views","types":[{"name":"ViewsItem","render":"Remember {text}","properties":{"text":{"type":"string"}}}]},"views":[{"id":"all","type":"ViewsItem","limit":3}]}""")], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private sealed class Reader : IModuleReader
    {
        public string CallerModuleId => "com.example.views";
        public ModuleQuery? Query { get; private set; }
        public ModuleReadContext? Context { get; private set; }
        public int Calls { get; private set; }
        public ModuleReadResult Result { get; init; } = new() { Status = ModuleReadStatus.Succeeded };
        public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
        { Calls++; Query = query; Context = context; return Task.FromResult(Result); }
    }
}
