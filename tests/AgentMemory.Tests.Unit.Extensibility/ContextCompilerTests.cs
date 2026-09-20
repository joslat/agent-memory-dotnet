using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The compiler's contract: what it assembles, and what it says about what it did not.
/// </summary>
public sealed class ContextCompilerTests
{
    private static ContextRequest Request() => new() { SessionId = "s-1", Query = "anything?" };

    private static ContextCompiler Compiler(params IContextContributor[] contributors) =>
        new(contributors, TimeProvider.System, NullLogger<ContextCompiler>.Instance);

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
