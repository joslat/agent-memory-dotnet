using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleIngestionTests
{
    [Fact]
    public async Task ResolvesOwnerBeforeProvidersAndCommitsDependenciesBeforeDependents()
    {
        var events = new List<string>();
        var first = Provider("com.example.first:writer", async request =>
        { events.Add("propose-first:" + request.Owner); await Task.Yield(); return [Proposal()]; });
        var second = Provider("com.example.second:writer", request =>
        { events.Should().Contain("commit-first"); events.Add("propose-second:" + request.Owner); return Task.FromResult<IReadOnlyList<ModuleProposal>>([Proposal()]); });
        var policy = Policy();
        policy.ResolveWriteOwner("claimed", "module.ingest", MemoryOperationAccess.Tenant).Returns(_ => { events.Add("owner"); return "resolved"; });
        var store1 = Store(batch => { batch.Request.Owner.Should().Be("resolved"); events.Add("commit-first"); return Success(batch); });
        var store2 = Store(batch => { events.Add("commit-second"); return Success(batch); });
        var engine = Engine([second, first], policy, new() { ["com.example.first"] = store1, ["com.example.second"] = store2 });
        var report = await engine.IngestAsync(Request());
        events.Should().Equal("owner", "propose-first:resolved", "commit-first", "propose-second:resolved", "commit-second");
        report.ResolvedOwner.Should().Be("resolved");
        report.Providers.Select(p => p.Status).Should().OnlyContain(s => s == ProviderIngestionStatus.Succeeded);
        report.Recorded.Should().HaveCount(2);
    }

    [Fact]
    public async Task DeniedOwnerMakesNoProviderStoreOrClockCalls()
    {
        var provider = Provider("com.example.first:writer");
        var policy = Policy();
        policy.ResolveWriteOwner(Arg.Any<string?>(), "module.ingest", MemoryOperationAccess.Tenant).Returns(_ => throw new MemoryOwnerScopeRequiredException("module.ingest"));
        var clock = new CountingClock();
        var store = Store();
        var report = await Engine([provider], policy, new() { ["com.example.first"] = store }, clock).IngestAsync(Request());
        report.Denied.Should().BeTrue(); report.Providers.Should().BeEmpty();
        provider.Calls.Should().Be(0); store.Calls.Should().Be(0); clock.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EmptyProviderSetHasNoPolicyClockOrStoreWork()
    {
        var policy = Policy(); var clock = new CountingClock();
        var report = await Engine([], policy, new(), clock).IngestAsync(Request());
        report.Recorded.Should().BeEmpty(); report.Providers.Should().BeEmpty();
        policy.ReceivedCalls().Should().BeEmpty(); clock.Calls.Should().Be(0);
        report.ResolvedOwner.Should().BeNull("an unresolved host claim must not be presented as a resolved owner");
    }

    [Fact]
    public async Task EveryTurnSkipsOnRequestAndExplicitSelectionDoesNotInvokeOtherProviders()
    {
        var automatic = Provider("com.example.first:auto");
        var manual = Provider("com.example.first:manual", trigger: IngestionTrigger.OnRequest);
        var store = Store(); var engine = Engine([manual, automatic], stores: new() { ["com.example.first"] = store });
        await engine.IngestAsync(Request());
        automatic.Calls.Should().Be(1); manual.Calls.Should().Be(0);
        await engine.IngestAsync(Request() with { Trigger = IngestionTrigger.OnRequest, ProviderIds = [manual.Descriptor.Id] });
        automatic.Calls.Should().Be(1); manual.Calls.Should().Be(1);
        var bad = () => engine.IngestAsync(Request() with { Trigger = IngestionTrigger.OnRequest, ProviderIds = ["com.example.first:missing"] });
        await bad.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AdmitsBeforeWritingOneBatchAndPreservesOriginalProposalIndices()
    {
        var provider = Provider("com.example.first:writer", _ => Task.FromResult<IReadOnlyList<ModuleProposal>>([
            Proposal(), Proposal() with { Type = "Entity" }, Proposal() with { Key = "other" }]));
        var store = Store(batch => { batch.Proposals.Select(p => p.Index).Should().Equal(0, 2); return Success(batch); });
        var report = await Engine([provider], stores: new() { ["com.example.first"] = store }).IngestAsync(Request());
        store.Calls.Should().Be(1);
        var outcome = report.Providers.Single(); outcome.Status.Should().Be(ProviderIngestionStatus.Partial);
        outcome.Proposals.Select(p => p.Index).Should().Equal(0, 1, 2);
        outcome.Proposals[1].Status.Should().Be(ProposalWriteStatus.Refused);
        report.Recorded.Single().Proposals.Should().HaveCount(3, "replay must retain refused proposals too");
    }

    [Fact]
    public async Task AllRefusedAndEmptyBatchesMakeNoStoreCalls()
    {
        var invalid = Provider("com.example.first:invalid", _ => Task.FromResult<IReadOnlyList<ModuleProposal>>([Proposal() with { Key = "" }]));
        var empty = Provider("com.example.first:empty", _ => Task.FromResult<IReadOnlyList<ModuleProposal>>([]));
        var store = Store(); var report = await Engine([invalid, empty], stores: new() { ["com.example.first"] = store }).IngestAsync(Request());
        store.Calls.Should().Be(0);
        report.Providers.Single(p => p.ProviderId == invalid.Descriptor.Id).Status.Should().Be(ProviderIngestionStatus.Refused);
        report.Providers.Single(p => p.ProviderId == empty.Descriptor.Id).Status.Should().Be(ProviderIngestionStatus.Succeeded);
    }

    [Fact]
    public async Task ProviderOrStoreFailureIsVisibleAndDoesNotEraseEarlierProgress()
    {
        var first = Provider("com.example.first:writer");
        var second = Provider("com.example.second:writer", _ => throw new InvalidOperationException("private detail"));
        var store = Store(); var report = await Engine([second, first], stores: new() { ["com.example.first"] = store, ["com.example.second"] = store }).IngestAsync(Request());
        report.Providers.Select(p => p.Status).Should().Equal(ProviderIngestionStatus.Succeeded, ProviderIngestionStatus.Unavailable);
        report.Providers[1].Detail.Should().NotContain("private detail");
        report.Recorded.Should().HaveCount(1); store.Calls.Should().Be(1);
        var failing = Store(_ => throw new IOException("commit acknowledgement lost"));
        report = await Engine([first], stores: new() { ["com.example.first"] = failing }).IngestAsync(Request());
        report.Recorded.Should().HaveCount(1);
        report.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Unavailable);
        report.Providers.Single().Detail.Should().Contain("commit status is unknown");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPropagatesWithoutRunningLaterProviders(bool atStore)
    {
        var first = Provider("com.example.first:writer", _ => atStore ? Task.FromResult<IReadOnlyList<ModuleProposal>>([Proposal()]) : throw new OperationCanceledException());
        var second = Provider("com.example.second:writer");
        var store = Store(_ => throw new OperationCanceledException());
        var action = () => Engine([second, first], stores: new() { ["com.example.first"] = store, ["com.example.second"] = store }).IngestAsync(Request());
        await action.Should().ThrowAsync<OperationCanceledException>(); second.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReplayUsesFrozenClockAndProposalsWithoutCallingProvidersOrCurrentClock()
    {
        var clock = new CountingClock(); var provider = Provider("com.example.first:writer"); var store = Store();
        var engine = Engine([provider], stores: new() { ["com.example.first"] = store }, clock: clock);
        var original = await engine.IngestAsync(Request());
        var encoded = JsonSerializer.Serialize(original.Recorded);
        var recorded = JsonSerializer.Deserialize<RecordedProposals[]>(encoded)!;
        var initialCalls = clock.Calls;
        var replay = await engine.ReplayAsync(Request(), recorded);
        provider.Calls.Should().Be(1); clock.Calls.Should().Be(initialCalls);
        store.Batches.Should().HaveCount(2);
        store.Batches[1].AtUtc.Should().Be(store.Batches[0].AtUtc);
        JsonSerializer.Serialize(store.Batches[1]).Should().Be(JsonSerializer.Serialize(store.Batches[0]));
        replay.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Succeeded);
    }

    [Fact]
    public async Task ReplayRejectsAnyLateInvalidRecordBeforeWritingEarlierRecords()
    {
        var first = Provider("com.example.first:writer"); var second = Provider("com.example.second:writer");
        var store = Store(); var engine = Engine([second, first], stores: new() { ["com.example.first"] = store, ["com.example.second"] = store });
        var initial = await engine.IngestAsync(Request());
        var records = initial.Recorded.ToArray(); records[1] = records[1] with { RequestFingerprint = "changed" };
        var action = () => engine.ReplayAsync(Request(), records);
        await action.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(2);
        action = () => engine.ReplayAsync(Request() with { Owner = "other" }, initial.Recorded);
        await action.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(2);
        action = () => engine.ReplayAsync(Request(), [initial.Recorded[0], initial.Recorded[0]]);
        await action.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(2);
    }

    [Fact]
    public async Task CallerAndProviderMutationCannotChangeSnapshots()
    {
        var messages = new List<IngestedMessage> { new() { Id = "m1", Role = "user", Text = "original" } };
        var properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("lesson") };
        var provider = Provider("com.example.first:writer", async request =>
        {
            messages.Clear(); request.Messages.Single().Text.Should().Be("original");
            await Task.Yield(); return [Proposal() with { Properties = properties }];
        });
        var store = Store(batch => { properties.Clear(); batch.Proposals[0].Proposal.Properties.Should().ContainKey("text"); return Success(batch); });
        var report = await Engine([provider], stores: new() { ["com.example.first"] = store }).IngestAsync(Request() with { Messages = messages });
        report.Recorded.Single().Proposals[0].Properties.Should().ContainKey("text");
    }

    [Fact]
    public void ConstructorRejectsDuplicateForeignOrUnboundProvidersBeforeInvocation()
    {
        var provider = Provider("com.example.first:writer"); var store = Store();
        Action duplicate = () => Engine([provider, provider], stores: new() { ["com.example.first"] = store });
        duplicate.Should().Throw<ArgumentException>();
        Action missing = () => Engine([provider], stores: new()); missing.Should().Throw<ArgumentException>();
        Action foreign = () => Engine([Provider("com.other.module:writer")], stores: new()); foreign.Should().Throw<ArgumentException>();
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task CannotRecordUndefinedJsonOrWriteAnyPartOfThatMalformedBatch()
    {
        var malformed = Proposal() with { Properties = new Dictionary<string, JsonElement> { ["text"] = default } };
        var provider = Provider("com.example.first:writer", _ => Task.FromResult<IReadOnlyList<ModuleProposal>>([Proposal(), malformed]));
        var store = Store();
        var report = await Engine([provider], stores: new() { ["com.example.first"] = store }).IngestAsync(Request());
        store.Calls.Should().Be(0);
        report.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Unavailable);
        report.Recorded.Should().BeEmpty();
        var serialize = () => JsonSerializer.Serialize(report);
        serialize.Should().NotThrow();
    }

    [Fact]
    public async Task CancellationAfterAnIgnoringEmptyProviderStillPropagates()
    {
        using var stop = new CancellationTokenSource();
        var provider = Provider("com.example.first:writer", _ => { stop.Cancel(); return Task.FromResult<IReadOnlyList<ModuleProposal>>([]); });
        var action = () => Engine([provider]).IngestAsync(Request(), stop.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ContradictoryStoreReceiptDoesNotClaimSuccessfulPersistence()
    {
        var provider = Provider("com.example.first:writer");
        var store = Store(batch => Success(batch).Select(o => o with { Errors = ["refused"] }).ToArray());
        var report = await Engine([provider], stores: new() { ["com.example.first"] = store }).IngestAsync(Request());
        report.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Unavailable);
        report.Providers.Single().Detail.Should().Contain("commit status is unknown");
    }

    [Fact]
    public async Task DependentWaitsForStoreCompletionAndReceivesTheCancellationToken()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Provider("com.example.first:writer"); var second = Provider("com.example.second:writer");
        using var stop = new CancellationTokenSource();
        var store = new AsyncStore(async (batch, token) => { token.Should().Be(stop.Token); entered.SetResult(); await release.Task; return Success(batch); });
        var engine = Engine([second, first], stores: new() { ["com.example.first"] = store, ["com.example.second"] = Store() });
        var work = engine.IngestAsync(Request(), stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second.Calls.Should().Be(0); work.IsCompleted.Should().BeFalse();
            first.LastToken.Should().Be(stop.Token);
        }
        finally { release.TrySetResult(); }
        await work; second.Calls.Should().Be(1); second.LastToken.Should().Be(stop.Token);
    }

    [Fact]
    public async Task ReplayRechecksOwnerDenialAndMetadataBeforeAnyStoreCall()
    {
        var policy = Policy(); var provider = Provider("com.example.first:writer"); var store = Store();
        var engine = Engine([provider], policy, new() { ["com.example.first"] = store });
        var original = await engine.IngestAsync(Request());
        var action = () => engine.ReplayAsync(Request(), [original.Recorded[0] with { ManifestFingerprint = "other-version" }]);
        await action.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(1);
        action = () => engine.ReplayAsync(Request(), [original.Recorded[0] with { Provider = original.Recorded[0].Provider with { Trigger = IngestionTrigger.OnRequest } }]);
        await action.Should().ThrowAsync<ArgumentException>(); store.Calls.Should().Be(1);
        policy.ResolveWriteOwner(Arg.Any<string?>(), "module.ingest", MemoryOperationAccess.Tenant).Returns(_ => throw new MemoryOwnerScopeRequiredException("module.ingest"));
        (await engine.ReplayAsync(Request(), original.Recorded)).Denied.Should().BeTrue();
        store.Calls.Should().Be(1); provider.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("invalid-enum")]
    [InlineData("missing-id")]
    public async Task MalformedStoreReceiptsAreUnavailableWithUnknownCommitStatus(string kind)
    {
        var provider = Provider("com.example.first:writer", _ => Task.FromResult<IReadOnlyList<ModuleProposal>>([Proposal(), Proposal() with { Key = "other" }]));
        var store = Store(batch => kind switch
        {
            "missing" => [Success(batch)[0]],
            "duplicate" => [Success(batch)[0], Success(batch)[0]],
            "foreign" => [Success(batch)[0], Success(batch)[1] with { Index = 99 }],
            "invalid-enum" => [Success(batch)[0], Success(batch)[1] with { Status = (ProposalWriteStatus)99 }],
            _ => [Success(batch)[0], Success(batch)[1] with { RecordId = null }]
        });
        var report = await Engine([provider], stores: new() { ["com.example.first"] = store }).IngestAsync(Request());
        report.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Unavailable);
        report.Providers.Single().Proposals.Should().BeEmpty();
        report.Recorded.Should().HaveCount(1);
    }

    private static ModuleIngestion Engine(IReadOnlyList<TestProvider> providers, IMemoryIsolationPolicy? policy = null,
        Dictionary<string, IModuleStore>? stores = null, CountingClock? clock = null) => new(Resolution(), providers,
            stores ?? new() { ["com.example.first"] = Store() }, policy ?? Policy(), clock ?? new(), NullLogger<ModuleIngestion>.Instance);
    private static ModuleResolution Resolution() => ModuleResolver.Resolve([
        ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.first","version":"0.1.0","sdk":"[0.1,0.2)","ontology":{"labelPrefix":"First","types":[{"name":"FirstItem","properties":{"text":{"type":"string","required":true}}}]}}"""),
        ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.second","version":"0.1.0","sdk":"[0.1,0.2)","requires":{"modules":{"com.example.first":"[0.1,0.2)"}},"ontology":{"labelPrefix":"Second","types":[{"name":"SecondItem","properties":{"text":{"type":"string","required":true}}}]}}""")
    ], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private static IMemoryIsolationPolicy Policy()
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveWriteOwner(Arg.Any<string?>(), "module.ingest", MemoryOperationAccess.Tenant).Returns(call => call.ArgAt<string?>(0));
        return policy;
    }
    private static IngestionRequest Request() => new() { SessionId = "session", Owner = "claimed", AtUtc = DateTimeOffset.Parse("2026-10-14T10:00:00Z"), Messages = [new() { Id = "m1", Role = "user", Text = "lesson" }] };
    private static ModuleProposal Proposal() => new() { Type = "FirstItem", Key = "lesson", Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement("lesson") }, Source = new() { MessageIds = ["m1"], Excerpt = "lesson" } };
    private static TestProvider Provider(string id, Func<IngestionRequest, Task<IReadOnlyList<ModuleProposal>>>? propose = null, IngestionTrigger trigger = IngestionTrigger.EveryTurn) => new(new() { Id = id, Trigger = trigger }, async request =>
    {
        var result = propose is null ? new[] { Proposal() } : await propose(request);
        return id.StartsWith("com.example.second:", StringComparison.Ordinal) ? result.Select(p => p with { Type = "SecondItem" }).ToArray() : result;
    });
    private static TestStore Store(Func<ModuleWriteBatch, IReadOnlyList<ProposalOutcome>>? write = null) => new(write ?? Success);
    private static IReadOnlyList<ProposalOutcome> Success(ModuleWriteBatch batch) => batch.Proposals.Select(p => new ProposalOutcome { Index = p.Index, Status = ProposalWriteStatus.Stored, RecordId = "record:" + p.Index }).ToArray();
    private sealed class TestProvider(IngestionProviderDescriptor descriptor, Func<IngestionRequest, Task<IReadOnlyList<ModuleProposal>>> propose) : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor => descriptor;
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default) { Calls++; LastToken = cancellationToken; return propose(request); }
    }
    private sealed class TestStore(Func<ModuleWriteBatch, IReadOnlyList<ProposalOutcome>> write) : IModuleStore
    {
        public int Calls { get; private set; }
        public List<ModuleWriteBatch> Batches { get; } = [];
        public Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default) { Calls++; Batches.Add(batch); return Task.FromResult(write(batch)); }
    }
    private sealed class AsyncStore(Func<ModuleWriteBatch, CancellationToken, Task<IReadOnlyList<ProposalOutcome>>> write) : IModuleStore
    {
        public Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default) => write(batch, cancellationToken);
    }
    private sealed class CountingClock : TimeProvider
    {
        public int Calls { get; private set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-14T12:00:00Z").AddSeconds(Calls++);
    }
}
