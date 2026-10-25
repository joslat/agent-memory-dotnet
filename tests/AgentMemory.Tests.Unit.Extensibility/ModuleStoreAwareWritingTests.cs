using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// EXT-write (design 0.12, W3): a provider sees its module's existing records, may name records its new one replaces, and
/// the host's closing approver decides each replacement once, when the batch is written. An approved closing invalidates
/// the replaced record in the same transaction; replay applies recorded decisions without asking again.
/// </summary>
public sealed class ModuleStoreAwareWritingTests
{
    private const string Module = "com.example.lessons";
    private const string Provider = Module + ":writer";

    private static readonly ModuleResolution Resolution = ModuleResolver.Resolve([ModuleManifest.Parse($$$"""
        {"format":"agentmemory.module/v1alpha1","id":"{{{Module}}}","version":"0.1.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Lesson","types":[{"name":"Lesson","properties":{"text":{"type":"string","required":true}},"render":"{text}"}]},
         "views":[{"id":"current","type":"Lesson","limit":50}]}
        """)], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });

    private static IngestionRequest Request() => new()
    {
        SessionId = "session", Owner = "ana", AtUtc = DateTimeOffset.Parse("2026-10-25T10:00:00Z"),
        Messages = [new() { Id = "m1", Role = "user", Text = "Use the retry helper, not a sleep loop." }],
    };

    private static ModuleProposal Lesson(string key, string text, params string[] replaces) => new()
    {
        Type = "Lesson", Key = key, Replaces = replaces,
        Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(text) },
        Source = new() { MessageIds = ["m1"], Excerpt = "Use the retry helper" },
    };

    private static ModuleViewItem Stored(string key, string text) => new()
    {
        Text = text,
        Record = new()
        {
            ModuleId = Module, Type = "Lesson", Id = "v-" + key, Key = key, OwnerId = "ana", CreatedAtUtc = DateTimeOffset.UnixEpoch,
            SourceSessionId = "earlier", Source = new() { MessageIds = ["m0"], Excerpt = "earlier" },
            Properties = new Dictionary<string, JsonElement> { ["text"] = JsonSerializer.SerializeToElement(text) },
        },
    };

    private sealed class Views(params ModuleViewItem[] items) : IModuleViews
    {
        public List<(string View, ModuleReadContext Context)> Calls { get; } = [];
        public ModuleReadStatus Status { get; init; } = ModuleReadStatus.Succeeded;

        public Task<ModuleViewResult> EvaluateAsync(string viewId, ModuleReadContext context, CancellationToken cancellationToken = default)
        {
            Calls.Add((viewId, context));
            return Task.FromResult(new ModuleViewResult { Status = Status, Items = Status == ModuleReadStatus.Succeeded ? items : [] });
        }
    }

    private sealed class Approver(Func<MemoryUpdatePair, MemoryClosingVerdict>? verdict = null, bool available = true, bool fail = false)
        : IMemoryClosingApprover
    {
        public List<MemoryUpdateRequest> Requests { get; } = [];
        public bool CanApprove(bool named) => available && named;

        public Task<IReadOnlyDictionary<string, MemoryClosingVerdict>> ApproveAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (fail) throw new HttpRequestException("judge down");
            return Task.FromResult<IReadOnlyDictionary<string, MemoryClosingVerdict>>(request.Pairs.ToDictionary(p => p.Key,
                p => (verdict ?? (_ => new MemoryClosingVerdict(0.9, true)))(p)));
        }
    }

    private sealed class ScriptedProvider(Func<IngestionRequest, IReadOnlyList<ModuleProposal>> propose, string? existingView = "current")
        : IIngestionProvider
    {
        public IngestionProviderDescriptor Descriptor { get; } = new() { Id = Provider, ExistingView = existingView, MaxExisting = 3 };
        public IngestionRequest? Seen { get; private set; }

        public Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default)
        {
            Seen = request;
            return Task.FromResult(propose(request));
        }
    }

    private sealed class Store : IModuleStore
    {
        public List<ModuleWriteBatch> Batches { get; } = [];

        public Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default)
        {
            Batches.Add(batch);
            return Task.FromResult<IReadOnlyList<ProposalOutcome>>([.. batch.Proposals.Select(p => new ProposalOutcome
            {
                Index = p.Index, RecordId = "r:" + p.Proposal.Key,
                Status = p.Proposal.Kind == ProposalKind.Invalidate ? ProposalWriteStatus.Invalidated : ProposalWriteStatus.Stored,
            })]);
        }
    }

    private static IMemoryIsolationPolicy Policy()
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveWriteOwner(Arg.Any<string?>(), "module.ingest", MemoryOperationAccess.Tenant).Returns(call => call.ArgAt<string?>(0));
        return policy;
    }

    private static ModuleIngestion Engine(IIngestionProvider provider, Store store, IModuleViews? views, IMemoryClosingApprover? approver) =>
        new(Resolution, [provider], new Dictionary<string, IModuleStore> { [Module] = store }, Policy(), TimeProvider.System,
            NullLogger<ModuleIngestion>.Instance, new ModuleIngestionServices { Views = views is null ? null : _ => views, Closings = approver });

    [Fact]
    public async Task AProviderSeesItsExistingRecordsForTheResolvedOwnerWithinItsLimits()
    {
        var views = new Views(Stored("a", "one"), Stored("b", "two"), Stored("c", "three"), Stored("d", "four"));
        var provider = new ScriptedProvider(_ => []);
        await Engine(provider, new Store(), views, null).IngestAsync(Request());

        provider.Seen!.Existing.Select(e => e.Record.Key).Should().Equal("a", "b", "c");
        views.Calls.Should().ContainSingle().Which.View.Should().Be("current");
        views.Calls[0].Context.Scope!.OwnerId.Should().Be("ana");
        views.Calls[0].Context.SessionId.Should().Be("session");
    }

    [Fact]
    public async Task AnApprovedReplacementClosesTheOldRecordInTheSameWrite()
    {
        var store = new Store();
        var approver = new Approver();
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper for flaky calls", "sleep-loop")]);
        var report = await Engine(provider, store, new Views(Stored("sleep-loop", "Wrap flaky calls in a sleep loop")), approver)
            .IngestAsync(Request());

        var pair = approver.Requests.Should().ContainSingle().Which;
        pair.Named.Should().BeTrue("a provider names the record it replaces");
        pair.Said.Should().Be("Use the retry helper, not a sleep loop.");
        pair.Pairs.Should().ContainSingle().Which.Should().Match<MemoryUpdatePair>(p =>
            p.NewMemory == "Use the retry helper for flaky calls" && p.StoredMemory == "Wrap flaky calls in a sleep loop");
        var batch = store.Batches.Should().ContainSingle("one transaction for the new record and the closing").Which;
        batch.Proposals.Select(p => (p.Proposal.Key, p.Proposal.Kind)).Should().Equal(
            ("retry-helper", ProposalKind.Assert), ("sleep-loop", ProposalKind.Invalidate));
        var outcome = report.Providers.Single().Proposals.Should().ContainSingle().Which;
        outcome.Status.Should().Be(ProposalWriteStatus.Stored);
        outcome.Closed.Should().Equal("sleep-loop");
        report.Recorded.Single().Closings.Should().ContainSingle().Which.Should().Match<RecordedClosing>(c =>
            c.Decision == ClosingDecision.Approved && c.Key == "sleep-loop" && c.ProposalIndex == 0 && c.Probability == 0.9);
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("no-approver")]
    [InlineData("unavailable")]
    [InlineData("failing")]
    [InlineData("no-answer")]
    public async Task WithoutAnApprovingVerdictNothingIsClosedAndTheNewRecordStillLands(string why)
    {
        var approver = why switch
        {
            "declined" => new Approver(_ => new MemoryClosingVerdict(0.1, false)),
            "unavailable" => new Approver(available: false),
            "failing" => new Approver(fail: true),
            "no-answer" => new Approver(_ => new MemoryClosingVerdict(null, false)),
            _ => null,
        };
        var store = new Store();
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper", "sleep-loop")]);
        var report = await Engine(provider, store, new Views(Stored("sleep-loop", "Use a sleep loop")), approver).IngestAsync(Request());

        store.Batches.Single().Proposals.Should().ContainSingle().Which.Proposal.Kind.Should().Be(ProposalKind.Assert);
        report.Providers.Single().Proposals.Single().Closed.Should().BeEmpty();
        report.Recorded.Single().Closings.Single().Decision.Should().Be(why == "declined" ? ClosingDecision.Declined : ClosingDecision.Unjudged);
    }

    [Fact]
    public async Task ARecordTheProviderWasNotShownIsNeverAskedAboutOrClosed()
    {
        var approver = new Approver();
        var store = new Store();
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper", "someone-elses")]);
        var report = await Engine(provider, store, new Views(Stored("sleep-loop", "Use a sleep loop")), approver).IngestAsync(Request());

        approver.Requests.Should().BeEmpty();
        report.Recorded.Single().Closings.Single().Decision.Should().Be(ClosingDecision.NotShown);
        store.Batches.Single().Proposals.Should().ContainSingle();
    }

    [Fact]
    public async Task AReplacingProposalThatIsRefusedClosesNothing()
    {
        var approver = new Approver();
        var store = new Store();
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper", "sleep-loop") with { Source = new() { MessageIds = ["unknown"], Excerpt = "x" } }]);
        var report = await Engine(provider, store, new Views(Stored("sleep-loop", "Use a sleep loop")), approver).IngestAsync(Request());

        approver.Requests.Should().BeEmpty("a refused proposal is not worth a judge call");
        store.Batches.Should().BeEmpty();
        report.Providers.Single().Proposals.Single().Status.Should().Be(ProposalWriteStatus.Refused);
    }

    [Fact]
    public async Task ReplayAppliesTheRecordedDecisionsWithoutAskingAgain()
    {
        var first = new Approver();
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper", "sleep-loop")]);
        var views = new Views(Stored("sleep-loop", "Use a sleep loop"));
        var recorded = (await Engine(provider, new Store(), views, first).IngestAsync(Request())).Recorded;

        var again = new Approver(_ => throw new InvalidOperationException("Replay must not ask a judge."));
        var store = new Store();
        var replay = await Engine(provider, store, views, again).ReplayAsync(Request(), recorded);

        again.Requests.Should().BeEmpty();
        store.Batches.Single().Proposals.Select(p => p.Proposal.Kind).Should().Equal(ProposalKind.Assert, ProposalKind.Invalidate);
        replay.Providers.Single().Proposals.Single().Closed.Should().Equal("sleep-loop");
    }

    [Theory]
    [InlineData(ProposalKind.Invalidate, "only an assertion")]
    [InlineData(ProposalKind.Assert, "own key")]
    public void AdmissionRefusesAReplacementThatIsNotOne(ProposalKind kind, string reason)
    {
        var proposal = kind == ProposalKind.Invalidate
            ? Lesson("a", "x", "b") with { Kind = ProposalKind.Invalidate, Properties = new Dictionary<string, JsonElement>() }
            : Lesson("a", "x", "a");
        var result = ModuleProposalAdmission.Admit(Resolution, new() { Id = Provider }, Request(), proposal);
        result.Accepted.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AnExistingViewMustBeDeclaredAndHaveViewsToReadFrom()
    {
        var undeclared = new ScriptedProvider(_ => [], existingView: "missing");
        FluentActions.Invoking(() => Engine(undeclared, new Store(), new Views(), null)).Should().Throw<ArgumentException>()
            .WithMessage("*view 'missing'*");
        FluentActions.Invoking(() => Engine(new ScriptedProvider(_ => []), new Store(), null, null)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task AFailingViewLeavesTheProviderWithoutExistingRecordsButStillRuns()
    {
        var provider = new ScriptedProvider(_ => [Lesson("retry-helper", "Use the retry helper")]);
        var report = await Engine(provider, new Store(), new Views(Stored("a", "one")) { Status = ModuleReadStatus.Refused }, null)
            .IngestAsync(Request());
        provider.Seen!.Existing.Should().BeEmpty();
        report.Providers.Single().Status.Should().Be(ProviderIngestionStatus.Succeeded);
    }

    [Fact]
    public void ExistingRecordsAreNotPartOfTheRequestsIdentity()
    {
        var plain = JsonSerializer.Serialize(Request());
        JsonSerializer.Serialize(Request() with { Existing = [Stored("a", "one")] }).Should().Be(plain);
    }
}
