using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The compiler's contract: what it assembles, and what it says about what it did not.
/// </summary>
public sealed class ContextCompilerTests
{
    private static ContextRequest Request() => new() { SessionId = "s-1", Query = "anything?" };

    /// <summary>A policy that passes the claimed owner through, so these tests isolate the compiler.</summary>
    private static IMemoryIsolationPolicy PassThroughPolicy()
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(
                Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<MemoryOperationAccess>())
            .Returns(call => MemoryScope.For(call.ArgAt<string?>(1) ?? "anonymous"));
        return policy;
    }

    private static ContextCompiler Compiler(params IContextContributor[] contributors) =>
        new(contributors, PassThroughPolicy(), TimeProvider.System, NullLogger<ContextCompiler>.Instance);

    /// <summary>A contributor that applies and produces lands as a section.</summary>
    [Fact]
    public async Task AnApplicableContributorBecomesASection()
    {
        var envelope = await Compiler(new StubContributor("notes", applies: true))
            .CompileAsync(Request());

        envelope.Sections.Should().ContainSingle().Which.ContributorId.Should().Be("notes");
        envelope.Omissions.Should().BeEmpty();
    }

    /// <summary>
    /// "Did not apply" and "applied and found nothing" are different facts and are recorded apart.
    /// </summary>
    /// <remarks>
    /// Collapsing them is how a reader loses the ability to tell an incomplete answer from a complete
    /// one — the same distinction the core assembler already draws between a section that was searched
    /// and empty and one abandoned to the latency budget.
    /// </remarks>
    [Fact]
    public async Task ApplyingAndFindingNothingIsNotTheSameAsNotApplying()
    {
        var envelope = await Compiler(
                new StubContributor("did-not-apply", applies: false),
                new StubContributor("found-nothing", applies: true, produces: false))
            .CompileAsync(Request());

        envelope.Sections.Should().BeEmpty();
        envelope.Omissions.Should().HaveCount(2);

        // BY REASON, not by a detail string. The first version of this test asserted both were
        // NotApplicable and called the difference "told apart by detail" -- which encoded the defect
        // rather than catching it: a caller switching on the reason, which is what a typed reason is
        // for, could not have told them apart at all.
        envelope.Omissions.Single(o => o.ContributorId == "did-not-apply").Reason
            .Should().Be(ContextOmissionReason.NotApplicable);
        envelope.Omissions.Single(o => o.ContributorId == "found-nothing").Reason
            .Should().Be(ContextOmissionReason.SearchedAndEmpty);
    }

    /// <summary>
    /// A contributor that throws is an omission, and the others still run.
    /// </summary>
    /// <remarks>
    /// One module's unreachable store must not cost the host its core memory. This is the property
    /// that makes third-party modules safe to install at all.
    /// </remarks>
    [Fact]
    public async Task AFailingContributorIsOmittedAndDoesNotStopTheRest()
    {
        var envelope = await Compiler(
                new ThrowingContributor("broken"),
                new StubContributor("healthy", applies: true))
            .CompileAsync(Request());

        envelope.Sections.Should().ContainSingle().Which.ContributorId.Should().Be("healthy");
        var omission = envelope.Omissions.Should().ContainSingle().Subject;
        omission.ContributorId.Should().Be("broken");
        omission.Reason.Should().Be(ContextOmissionReason.Unavailable);
    }

    /// <summary>
    /// Cancellation propagates rather than being absorbed as an omission.
    /// </summary>
    /// <remarks>
    /// A cancelled request is the caller's decision. Recording it as "this contributor was
    /// unavailable" would report a module failure that did not happen and hide one that did.
    /// </remarks>
    [Fact]
    public async Task CancellationIsNotSwallowedAsAnOmission()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Compiler(new StubContributor("notes", applies: true))
            .CompileAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Sections come out in priority order, then by id — the same inputs give the same envelope.
    /// </summary>
    /// <remarks>
    /// An order that depended on registration or on which task finished first would make the rendered
    /// prompt vary between runs of one configuration, which is the property that makes a prompt
    /// diffable at all.
    /// </remarks>
    [Fact]
    public async Task SectionOrderIsDeterministic()
    {
        var envelope = await Compiler(
                new StubContributor("zzz", applies: true, priority: 10),
                new StubContributor("aaa", applies: true, priority: 50),
                new StubContributor("mmm", applies: true, priority: 10))
            .CompileAsync(Request());

        envelope.Sections.Select(s => s.ContributorId)
            .Should().ContainInOrder("mmm", "zzz", "aaa");
    }

    /// <summary>
    /// The snapshot records the RESOLVED owner, not the one the host claimed.
    /// </summary>
    /// <remarks>
    /// The field is documented as evidence that isolation acted. Copying the request through would
    /// make it a restatement of the input that looks like enforcement — and the one thing a caller
    /// would use it for is confirming enforcement happened.
    /// </remarks>
    [Fact]
    public async Task TheSnapshotCarriesTheResolvedOwnerNotTheClaimedOne()
    {
        var policy = Substitute.For<IMemoryIsolationPolicy>();
        policy.ResolveReadScope(
                Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<MemoryOperationAccess>())
            .Returns(MemoryScope.For("resolved-owner"));

        var compiler = new ContextCompiler(
            [], policy, TimeProvider.System, NullLogger<ContextCompiler>.Instance);

        var envelope = await compiler.CompileAsync(
            new ContextRequest { SessionId = "s-1", Owner = "claimed-owner" });

        envelope.Snapshot.Owner.Should().Be("resolved-owner");
    }

    /// <summary>The snapshot records the clocks the request pinned.</summary>
    [Fact]
    public async Task TheSnapshotCarriesTheAsOfPins()
    {
        var valid = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        var system = valid.AddDays(3);

        var envelope = await Compiler().CompileAsync(
            new ContextRequest { SessionId = "s-1", AsOf = valid, SystemAsOf = system });

        envelope.Snapshot.AsOf.Should().Be(valid);
        envelope.Snapshot.SystemAsOf.Should().Be(system);
    }

    private sealed class StubContributor(
        string id, bool applies, bool produces = true, int priority = 100) : IContextContributor
    {
        // `produces` is its own flag rather than a nullable section, so "applies and returns nothing"
        // is expressible at all -- the case this suite exists to distinguish.
        private readonly ContextSection? _section = produces
            ? new ContextSection($"{id}.section", id, 1, [new ContextItem(null, "x")])
            : null;

        public ContextContributorDescriptor Descriptor { get; } =
            new(id, new HashSet<string>(StringComparer.Ordinal) { $"{id}.section" }, priority);

        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(applies);

        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(_section);
    }

    private sealed class ThrowingContributor(string id) : IContextContributor
    {
        public ContextContributorDescriptor Descriptor { get; } =
            new(id, new HashSet<string>(StringComparer.Ordinal));

        public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the module's store is down");
    }
}

/// <summary>
/// The core contributor's mapping onto the existing recall request.
/// </summary>
public sealed class CoreContributorTests
{
    /// <summary>
    /// Recall options are left at the DEFAULT INSTANCE so the host's configuration still applies.
    /// </summary>
    /// <remarks>
    /// The assembler falls back to <c>MemoryOptions.Recall</c> only when the request's Options is the
    /// Default instance by reference. Passing a copy of the defaults would look identical and would
    /// silently opt the host out of its own configuration — a defect invisible in any assertion about
    /// field values, which is why this one asserts the reference.
    /// </remarks>
    [Fact]
    public async Task TheRecallRequestKeepsTheDefaultOptionsReference()
    {
        var assembler = Substitute.For<IMemoryContextAssembler>();
        assembler.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "s-1", AssembledAtUtc = DateTimeOffset.UnixEpoch });

        await new CoreMemoryContextContributor(assembler)
            .ContributeAsync(new ContextRequest { SessionId = "s-1" }, CancellationToken.None);

        await assembler.Received(1).AssembleContextAsync(
            Arg.Is<RecallRequest>(r => ReferenceEquals(r.Options, RecallOptions.Default)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A point-in-time request uses the as-of overload with BOTH clocks.</summary>
    [Fact]
    public async Task APointInTimeRequestUsesBothClocks()
    {
        var assembler = Substitute.For<IMemoryContextAssembler>();
        var valid = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        assembler.AssembleContextAsOfAsync(
                Arg.Any<RecallRequest>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(new MemoryContext { SessionId = "s-1", AssembledAtUtc = DateTimeOffset.UnixEpoch });

        await new CoreMemoryContextContributor(assembler).ContributeAsync(
            new ContextRequest { SessionId = "s-1", AsOf = valid }, CancellationToken.None);

        // SystemAsOf defaults to AsOf rather than to the machine clock: answering "what was true
        // then, as believed now" is the defect the two-clock contract exists to prevent.
        await assembler.Received(1).AssembleContextAsOfAsync(
            Arg.Any<RecallRequest>(), valid, valid, Arg.Any<CancellationToken>());
        await assembler.DidNotReceive().AssembleContextAsync(
            Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }
}
