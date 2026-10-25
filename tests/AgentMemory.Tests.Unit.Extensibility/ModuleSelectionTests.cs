using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleSelectionTests
{
    private const string Module = "com.example.selection";

    [Fact]
    public async Task OffNeverResolvesJudgeAndRendersAdmittedItems()
    {
        var renderer = new Renderer();
        var result = await new ModuleContextSelection(new(), () => throw new InvalidOperationException("Gate must stay unresolved"))
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "remember this")])), renderer);
        result.RenderedText.Should().Be("[remember this]"); result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Disabled);
        renderer.AdmissionCalls.Should().Be(1); result.Decisions.Single().Disposition.Should().Be(ModuleCandidateDisposition.Kept);
    }

    [Fact]
    public async Task AdmissionPrecedesJudgeAndBlockedTextNeverReachesIt()
    {
        var renderer = new Renderer { Admission = c => c.Text != "blocked" };
        var gate = new Gate(request =>
        {
            renderer.AdmissionCalls.Should().Be(2);
            request.Candidates.Select(c => c.Text).Should().Equal("good");
            request.Candidates.Single().MemoryType.Should().Be(Module);
            request.Conversation.Select(t => t.Text).Should().Equal("earlier", "prior");
            return Complete(request, 1);
        });
        var result = await new ModuleContextSelection(new() { UseJudge = true }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "blocked"), new("two", "good")])), renderer);
        result.RenderedText.Should().Be("[good]"); result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Applied);
        result.Decisions.Single(d => d.Candidate.Text == "blocked").Disposition.Should().Be(ModuleCandidateDisposition.Denied);
        gate.Calls.Should().Be(1); result.AnsweredBy.Should().Be("fixture");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative")]
    [InlineData("over-one")]
    public async Task InvalidDecisionRetainsOnlyBoundedAdmittedItems(string kind)
    {
        var gate = new Gate(request =>
        {
            var scores = request.Candidates.ToDictionary(c => c.Key, _ => 1.0);
            var key = request.Candidates[0].Key;
            if (kind == "missing") scores.Remove(key);
            else if (kind == "extra") scores.Add("not-offered", 1);
            else scores[key] = kind switch { "nan" => double.NaN, "infinity" => double.PositiveInfinity, "negative" => -0.1, _ => 1.1 };
            return new(scores, "fixture");
        });
        var result = await new ModuleContextSelection(new() { UseJudge = true, MaxItems = 1 }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "first"), new("two", "second")])), new Renderer());
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.InvalidDecision); result.RenderedText.Should().Be("[first]");
        result.Decisions.Should().OnlyContain(d => d.Score == null);
        result.Decisions.Single(d => d.Candidate.ItemId == "two").Disposition.Should().Be(ModuleCandidateDisposition.ItemLimit);
        gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ValidAllNegativeDecisionIsFinalAndNeverFallsBack()
    {
        var gate = new Gate(request => Complete(request, 0)); var renderer = new Renderer();
        var result = await new ModuleContextSelection(new() { UseJudge = true }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "first")])), renderer);
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Applied); result.RenderedText.Should().BeEmpty();
        result.Decisions.Single().Disposition.Should().Be(ModuleCandidateDisposition.JudgeRejected);
        renderer.RenderCalls.Should().Be(0); gate.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProviderFailureFallsBackButCallerCancellationPropagates()
    {
        var envelope = await Envelope(new Spec(Module, "items", [new("one", "first"), new("two", "second")]));
        var gate = new Gate(_ => throw new InvalidOperationException("private provider failure"));
        var result = await new ModuleContextSelection(new() { UseJudge = true, MaxItems = 1 }, () => gate).SelectAsync(envelope, new Renderer());
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Unavailable); result.RenderedText.Should().Be("[first]");
        using var source = new CancellationTokenSource();
        var cancelling = new Gate(_ => { source.Cancel(); throw new OperationCanceledException(source.Token); });
        Func<Task> action = () => new ModuleContextSelection(new() { UseJudge = true }, () => cancelling)
            .SelectAsync(envelope, new Renderer(), source.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DuplicateRecordRetainsAllOriginsWithoutDuplicateJudgeKeys()
    {
        var first = Stored("version-1", "same text", "source-a");
        var second = Stored("version-1", "same text", "source-b");
        var gate = new Gate(request => { request.Candidates.Should().ContainSingle(); return Complete(request, 1); });
        var result = await new ModuleContextSelection(new() { UseJudge = true }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "a", [first]), new Spec(Module, "b", [second])), new Renderer());
        result.RenderedText.Should().Be("[same text]");
        result.Decisions.Single().Origins.SelectMany(c => c.Provenance!.SourceMessageIds).Should().Equal("source-a", "source-b");
    }

    [Fact]
    public async Task ConflictingDuplicateIsNotJudgedAndUnrelatedModulesAreNotDeduplicated()
    {
        var renderer = new Renderer();
        var conflict = await new ModuleContextSelection(new() { UseJudge = true }, () => throw new InvalidOperationException("No gate for a conflict"))
            .SelectAsync(await Envelope(new Spec(Module, "a", [Stored("v1", "one", "a")]), new Spec(Module, "b", [Stored("v1", "different", "b")])), renderer);
        conflict.RenderedText.Should().BeEmpty(); conflict.Decisions.Single().Disposition.Should().Be(ModuleCandidateDisposition.DuplicateConflict);
        renderer.AdmissionCalls.Should().Be(0);
        var unrelated = await new ModuleContextSelection(new()).SelectAsync(await Envelope(
            new Spec(Module, "a", [new("same-id", "same")]), new Spec("com.example.other", "a", [new("same-id", "same")])), new Renderer());
        unrelated.RenderedText.Should().Be("[same|same]"); unrelated.Decisions.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("\u00e9", 4, "[\u00e9]")]
    [InlineData("<", 3, "")]
    [InlineData("<", 6, "[&lt;]")]
    [InlineData("\U0001F600", 5, "")]
    [InlineData("\U0001F600", 6, "[\U0001F600]")]
    public async Task OutputBudgetCountsExactUtf8AndEscapingWithoutTruncation(string text, int budget, string expected)
    {
        var result = await new ModuleContextSelection(new() { MaxRenderedUtf8Bytes = budget })
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", text)])), new Renderer());
        result.RenderedText.Should().Be(expected); result.RenderedUtf8Bytes.Should().Be(Encoding.UTF8.GetByteCount(expected));
        result.Decisions.Single().Candidate.Text.Should().Be(text);
    }

    [Fact]
    public async Task CollectionCapsAreObservableAndConfiguredLimitsAreSnapshotted()
    {
        var limits = new Dictionary<string, int> { [Module + ":a"] = 1 };
        var selector = new ModuleContextSelection(new() { MaxCandidates = 2, MaxItemsPerContributor = 3, ContributorItemLimits = limits });
        limits[Module + ":a"] = 100;
        var renderer = new Renderer();
        var result = await selector.SelectAsync(await Envelope(new Spec(Module, "a", [new("a1", "one"), new("a2", "two")]),
            new Spec(Module, "b", [new("b1", "three"), new("b2", "four")])), renderer);
        renderer.AdmissionCalls.Should().Be(2); result.RenderedText.Should().Be("[one|three]");
        result.Contributors.Select(c => c.OfferedItems).Should().Equal(2, 2);
        result.Contributors.Select(c => c.ItemLimitOmitted).Should().Equal(1, 1);
    }

    [Fact]
    public async Task ValidScoresRankBeforeOutputLimitsAndRenderFailuresAreIsolated()
    {
        var gate = new Gate(request => new(request.Candidates.ToDictionary(c => c.Key, c => c.Text == "best" ? 1.0 : 0.6), "fixture"));
        var result = await new ModuleContextSelection(new() { UseJudge = true, MaxItems = 1 }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("a", "ordinary"), new("b", "best")])), new Renderer());
        result.RenderedText.Should().Be("[best]");
        var failed = await new ModuleContextSelection(new()).SelectAsync(await Envelope(new Spec(Module, "items", [new("a", "boom"), new("b", "safe")])),
            new Renderer { ThrowOnText = "boom" });
        failed.RenderedText.Should().Be("[safe]"); failed.Decisions[0].Disposition.Should().Be(ModuleCandidateDisposition.Unavailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZeroOutputAllowanceDoesNotAdmitOrCallAJudge(bool itemLimit)
    {
        var calls = 0; var renderer = new Renderer();
        var options = itemLimit ? new ModuleSelectionOptions { UseJudge = true, MaxItems = 0 }
            : new ModuleSelectionOptions { UseJudge = true, MaxRenderedUtf8Bytes = 0 };
        var result = await new ModuleContextSelection(options, () => { calls++; return new Gate(r => Complete(r, 1)); })
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "would be wasted")])), renderer);
        calls.Should().Be(0); renderer.AdmissionCalls.Should().Be(0); renderer.RenderCalls.Should().Be(0);
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.NoCandidates); result.RenderedText.Should().BeEmpty();
        result.Decisions.Single().Disposition.Should().Be(itemLimit ? ModuleCandidateDisposition.ItemLimit : ModuleCandidateDisposition.OutputByteLimit);
    }

    [Fact]
    public async Task StageReceiptsDistinguishAdmissionFailureFromPostAdmissionRenderFailure()
    {
        var renderer = new Renderer { Admission = c => c.Text == "admission-failure"
            ? throw new InvalidOperationException("private admission detail") : c.Text != "denied", ThrowOnText = "render-failure" };
        var result = await new ModuleContextSelection(new()).SelectAsync(await Envelope(new Spec(Module, "items",
            [new("a", "admission-failure"), new("b", "denied"), new("c", "render-failure"), new("d", "safe")])), renderer);
        result.Decisions.Select(d => d.Admitted).Should().Equal(false, false, true, true);
        result.Decisions.Should().OnlyContain(d => !d.OfferedToJudge);
        result.Decisions.Select(d => d.Disposition).Should().Equal(ModuleCandidateDisposition.Unavailable,
            ModuleCandidateDisposition.Denied, ModuleCandidateDisposition.Unavailable, ModuleCandidateDisposition.Kept);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageReceiptsDistinguishMissingGateFromAttemptedFailedCall(bool call)
    {
        var result = await new ModuleContextSelection(new() { UseJudge = true },
                () => call ? new Gate(_ => throw new InvalidOperationException("provider failure")) : null)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("one", "safe")])), new Renderer());
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Unavailable);
        result.Decisions.Single().Admitted.Should().BeTrue();
        result.Decisions.Single().OfferedToJudge.Should().Be(call);
        result.RenderedText.Should().Be("[safe]");
    }

    [Fact]
    public async Task CandidateAndBatchByteLimitsAreAppliedBeforeTheirRespectiveExternalCalls()
    {
        var renderer = new Renderer();
        var gate = new Gate(r => { r.Candidates.Select(c => c.Text).Should().Equal("\u00e9"); return Complete(r, 1); });
        var result = await new ModuleContextSelection(new() { UseJudge = true, MaxCandidateUtf8Bytes = 2, MaxJudgeUtf8Bytes = 2 }, () => gate)
            .SelectAsync(await Envelope(new Spec(Module, "items", [new("a", "big"), new("b", "\u00e9"), new("c", "x")])), renderer);
        renderer.AdmissionCalls.Should().Be(2); gate.Calls.Should().Be(1);
        result.Decisions.Select(d => d.Disposition).Should().Equal(ModuleCandidateDisposition.CandidateByteLimit,
            ModuleCandidateDisposition.Kept, ModuleCandidateDisposition.JudgeByteLimit);
        result.Decisions.Select(d => d.Admitted).Should().Equal(false, true, true);
        result.Decisions.Select(d => d.OfferedToJudge).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task CallerCancellationAfterProviderReturnsCannotBecomeFallback()
    {
        using var source = new CancellationTokenSource();
        var gate = new Gate(r => { source.Cancel(); return Complete(r, 1); });
        var envelope = await Envelope(new Spec(Module, "items", [new("one", "safe")]));
        Func<Task> action = () => new ModuleContextSelection(new() { UseJudge = true }, () => gate)
            .SelectAsync(envelope, new Renderer(), source.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Extensibility 0.12, R1: candidates judged in the core gate's call ----

    [Fact]
    public async Task APooledSelectionOffersAdmittedBoundedCandidatesAndAppliesTheCoreGatesDecisions()
    {
        var renderer = new Renderer { Admission = c => c.Text != "blocked" };
        var gateCalls = 0;
        var selection = new ModuleContextSelection(new() { UseJudge = true, Threshold = 0.5 }, () => { gateCalls++; return null; });
        var turn = selection.PrepareForCorePool(await Envelope(new Spec(Module, "items",
            [new("one", "keep me"), new("two", "drop me"), new("three", "blocked")])), renderer);
        turn.Companions.Select(c => c.Text).Should().Equal("keep me", "drop me");
        turn.Companions.Should().OnlyContain(c => c.MemoryType == Module);
        var scores = turn.Companions.ToDictionary(c => c.Key, c => c.Text == "keep me" ? 0.9 : 0.1);
        scores["semantic:core-fact"] = 0.99;  // the core gate's answer also covers core memories
        var result = turn.Complete(scores, "jev", offeredToJudge: true);
        result.RenderedText.Should().Be("[keep me]");
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Applied);
        result.AnsweredBy.Should().Be("jev");
        result.Decisions.Single(d => d.Candidate.Text == "drop me").Disposition.Should().Be(ModuleCandidateDisposition.JudgeRejected);
        result.Decisions.Single(d => d.Candidate.Text == "blocked").Disposition.Should().Be(ModuleCandidateDisposition.Denied);
        gateCalls.Should().Be(0, "in a pooled selection the core gate judges; the module's own gate is never resolved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutCoreDecisionsAPooledSelectionKeepsItsBoundedFallback(bool offeredToJudge)
    {
        var turn = new ModuleContextSelection(new() { UseJudge = true, MaxItems = 1 })
            .PrepareForCorePool(await Envelope(new Spec(Module, "items", [new("one", "first"), new("two", "second")])), new Renderer());
        var result = turn.Complete(null, offeredToJudge: offeredToJudge);
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Unavailable);
        result.RenderedText.Should().Be("[first]");
        result.Decisions.Should().OnlyContain(d => d.OfferedToJudge == offeredToJudge && d.Score == null);
    }

    [Fact]
    public async Task AnIncompleteCoreDecisionIsInvalidAndATurnCompletesOnce()
    {
        var turn = new ModuleContextSelection(new() { UseJudge = true })
            .PrepareForCorePool(await Envelope(new Spec(Module, "items", [new("one", "first"), new("two", "second")])), new Renderer());
        var result = turn.Complete(new Dictionary<string, double> { [turn.Companions[0].Key] = 0.9 });
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.InvalidDecision);
        result.RenderedText.Should().Be("[first|second]", "an invalid decision keeps the bounded admitted items");
        FluentActions.Invoking(() => turn.Complete(null)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task APooledSelectionWithJudgingOffOffersNothing()
    {
        var turn = new ModuleContextSelection(new()).PrepareForCorePool(
            await Envelope(new Spec(Module, "items", [new("one", "first")])), new Renderer());
        turn.Companions.Should().BeEmpty();
        var result = turn.Complete(new Dictionary<string, double>());
        result.JudgeOutcome.Should().Be(ModuleJudgeOutcome.Disabled);
        result.RenderedText.Should().Be("[first]");
    }

    private static ContextItem Stored(string id, string text, string source) => new(id, text)
    {
        Provenance = new() { RecordModuleId = Module, RecordType = "Item", RecordVersionId = id, RecordKey = "key",
            CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "teaching", SourceMessageIds = [source] }
    };
    private static MemoryGateDecision Complete(MemoryGateRequest request, double score) => new(request.Candidates.ToDictionary(c => c.Key, _ => score), "fixture");
    private sealed record Spec(string Module, string Name, IReadOnlyList<ContextItem> Items);
    private static async Task<ContextEnvelope> Envelope(params Spec[] specs)
    {
        var resolution = ModuleResolver.Resolve(specs.Select(s => s.Module).Distinct().Select(id => ModuleManifest.Parse(JsonSerializer.Serialize(new
        { format = "agentmemory.module/v1alpha1", id, version = "1.0.0", sdk = "[0.1,0.2)" }))).ToArray(), ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<MemoryOperationAccess>()).Returns(MemoryScope.For("owner"));
        return await new ContextCompiler(resolution, specs.Select(s => new Contributor(s)), policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance)
            .CompileAsync(new() { SessionId = "fresh", Owner = "owner", Query = "What is due?", RecentTurn =
                [new(ChatRole.User, "earlier"), new(ChatRole.Assistant, "prior"), new(ChatRole.User, "What is due?")] });
    }
    private sealed class Contributor(Spec spec) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } = new(spec.Module + ":" + spec.Name, new HashSet<string> { spec.Module + ":items" });
        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ContextSection?>(new(spec.Module + ":items", Descriptor.Id, 1, spec.Items));
    }
    private sealed class Gate(Func<MemoryGateRequest, MemoryGateDecision> decide) : IMemoryGate
    {
        public int Calls { get; private set; }
        public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(decide(request)); }
    }
    private sealed class Renderer : IModuleCandidateRenderer
    {
        private readonly HashSet<ModuleContextCandidate> _admitted = new(ReferenceEqualityComparer.Instance);
        public Func<ModuleContextCandidate, bool> Admission { get; init; } = _ => true;
        public string? ThrowOnText { get; init; }
        public int AdmissionCalls { get; private set; }
        public int RenderCalls { get; private set; }
        public bool Admit(ModuleContextCandidate candidate)
        { AdmissionCalls++; var accepted = Admission(candidate); if (accepted) _admitted.Add(candidate); return accepted; }
        public string Render(IReadOnlyList<ModuleContextCandidate> candidates)
        {
            RenderCalls++;
            if (candidates.Any(c => !_admitted.Contains(c) || c.Text == ThrowOnText)) throw new InvalidOperationException("Cannot render these items");
            return "[" + string.Join("|", candidates.Select(c => c.Text.Replace("<", "&lt;", StringComparison.Ordinal))) + "]";
        }
    }
}
