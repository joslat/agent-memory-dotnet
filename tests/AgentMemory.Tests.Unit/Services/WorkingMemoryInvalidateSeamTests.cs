using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.4b seams 2–5: invalidation and preference deletion recompile the working-memory block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Supersede was hooked and its exact twin was not.</b> Every working-memory block query filters
/// <c>invalidated_at IS NULL</c>, so retracting a fact changes what the block should say — but
/// nothing recompiled it, and the block went on asserting the retracted value until some unrelated
/// write happened to trigger a rebuild. That is the staleness this design calls "manufactures
/// knowledge-update failures", which is the stated reason supersession is hooked at all.
/// </para>
/// <para>
/// Every test drives the real service method rather than calling <c>RebuildAsync</c>, because the
/// defect being fixed was a missing trigger, and a test that calls the thing directly cannot detect
/// a trigger that was never wired.
/// </para>
/// </remarks>
public sealed class WorkingMemoryInvalidateSeamTests
{
    private const string Owner = "owner-alice";
    private const string Id = "target-id";

    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IWorkingMemoryService _workingMemory = Substitute.For<IWorkingMemoryService>();

    private LongTermMemoryService CreateSut(bool enabled = true)
    {
        var memoryOptions = new MemoryOptions();
        memoryOptions.WorkingMemory.Enabled = enabled;

        return new LongTermMemoryService(
            _entities,
            _facts,
            _preferences,
            Substitute.For<IRelationshipRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance),
            _workingMemory,
            Options.Create(memoryOptions));
    }

    private static MemoryScope Scope => MemoryScope.For(Owner, includeShared: false);

    [Fact]
    public async Task InvalidateFactAsync_WhenItMatched_RebuildsTheBlock()
    {
        _facts.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        var changed = await CreateSut().InvalidateFactAsync(Id, Scope, CancellationToken.None);

        changed.Should().BeTrue("the decorator must return the repository result unchanged");
        await _workingMemory.Received(1).RebuildAsync(Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateEntityAsync_WhenItMatched_RebuildsTheBlock()
    {
        _entities.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().InvalidateEntityAsync(Id, Scope, CancellationToken.None);

        await _workingMemory.Received(1).RebuildAsync(Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidatePreferenceAsync_WhenItMatched_RebuildsTheBlock()
    {
        _preferences.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut().InvalidatePreferenceAsync(Id, Scope, CancellationToken.None);

        await _workingMemory.Received(1).RebuildAsync(Owner, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A scoped call that matched nothing changed nothing. Rebuilding there would spend reads on
    /// exactly the calls the isolation guard exists to make free.
    /// </summary>
    [Fact]
    public async Task InvalidateFactAsync_WhenItMatchedNothing_DoesNotRebuild()
    {
        _facts.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(false);

        var changed = await CreateSut().InvalidateFactAsync(Id, Scope, CancellationToken.None);

        changed.Should().BeFalse();
        await _workingMemory.DidNotReceiveWithAnyArgs().RebuildAsync(default!, default);
    }

    [Fact]
    public async Task InvalidateFactAsync_WhenTheTierIsOff_DoesNotRebuild()
    {
        _facts.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);

        await CreateSut(enabled: false).InvalidateFactAsync(Id, Scope, CancellationToken.None);

        await _workingMemory.DidNotReceiveWithAnyArgs().RebuildAsync(default!, default);
    }

    /// <summary>
    /// Deleting a preference rebuilds UNCONDITIONALLY, and the asymmetry with the invalidate family
    /// is forced rather than chosen: <c>IPreferenceRepository.DeleteAsync</c> returns <c>Task</c>,
    /// so there is no way to learn whether it matched. The choice is between a wasted rebuild on a
    /// no-op and a stale block on a real delete, and staleness is the failure being prevented.
    /// </summary>
    [Fact]
    public async Task DeletePreferenceAsync_RebuildsTheBlock()
    {
        await CreateSut().DeletePreferenceAsync(Id, Scope, CancellationToken.None);

        await _preferences.Received(1).DeleteAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _workingMemory.Received(1).RebuildAsync(Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreferenceAsync_WhenTheTierIsOff_DoesNotRebuild()
    {
        await CreateSut(enabled: false).DeletePreferenceAsync(Id, Scope, CancellationToken.None);

        await _preferences.Received(1).DeleteAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _workingMemory.DidNotReceiveWithAnyArgs().RebuildAsync(default!, default);
    }

    /// <summary>
    /// The write must still succeed when the projection cannot be recompiled — a caller who
    /// successfully retracted a fact must not see an exception from derived bookkeeping.
    /// </summary>
    [Fact]
    public async Task InvalidateFactAsync_WhenTheRebuildThrows_StillReportsSuccess()
    {
        _facts.InvalidateAsync(Id, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);
        _workingMemory.RebuildAsync(Owner, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("neo4j is having a day")));

        var changed = await CreateSut().InvalidateFactAsync(Id, Scope, CancellationToken.None);

        changed.Should().BeTrue("a failed projection must never fail the write that succeeded");
        await _workingMemory.Received(1).ClearAsync(Owner, Arg.Any<CancellationToken>());
    }
}
