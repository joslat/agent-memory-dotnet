using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The single working-memory rebuild epilogue and its failure policy (30.4b).
/// </summary>
/// <remarks>
/// <para>
/// This policy used to be written out twice — in <c>LongTermMemoryService</c> and in
/// <c>PersistenceStage</c> — and the two copies had already drifted: the identical
/// clear-also-failed branch logged at <c>Error</c> in one and <c>Warning</c> in the other. A third
/// copy was about to be added for the merge seam. These tests pin the one that replaced all of them.
/// </para>
/// </remarks>
public sealed class WorkingMemoryRebuilderTests
{
    private const string Owner = "owner-alice";

    private readonly IWorkingMemoryService _workingMemory = Substitute.For<IWorkingMemoryService>();

    private WorkingMemoryRebuilder CreateSut(
        WorkingMemoryOptions? options = null, bool registerTier = true) =>
        new(registerTier ? _workingMemory : null,
            options ?? new WorkingMemoryOptions { Enabled = true },
            NullLogger.Instance);

    [Fact]
    public async Task RebuildAsync_WhenEnabled_RecompilesTheOwnersBlock()
    {
        await CreateSut().RebuildAsync(Owner, "a test", CancellationToken.None);

        await _workingMemory.Received(1).RebuildAsync(Owner, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, true)]   // tier switched off entirely
    [InlineData(true, false)]   // tier on, rebuild-on-write off
    public async Task RebuildAsync_WhenGatedOff_DoesNothing(bool enabled, bool rebuildOnWrite)
    {
        var sut = CreateSut(new WorkingMemoryOptions
        {
            Enabled = enabled,
            RebuildOnWrite = rebuildOnWrite,
        });

        sut.IsDisabled.Should().BeTrue("callers skip their own setup work on this flag");
        await sut.RebuildAsync(Owner, "a test", CancellationToken.None);

        await _workingMemory.DidNotReceiveWithAnyArgs().RebuildAsync(default!, default);
    }

    [Fact]
    public async Task RebuildAsync_WhenTheTierWasNeverRegistered_DoesNothing()
    {
        var sut = CreateSut(registerTier: false);

        sut.IsDisabled.Should().BeTrue();
        await sut.RebuildAsync(Owner, "a test", CancellationToken.None);
    }

    /// <summary>
    /// Guard G3's near side: an ownerless write must not reach a MERGE on a null identity key.
    /// Every TCK bridge write is ownerless.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RebuildAsync_WithNoOwner_DoesNothing(string? ownerId)
    {
        await CreateSut().RebuildAsync(ownerId, "a test", CancellationToken.None);

        await _workingMemory.DidNotReceiveWithAnyArgs().RebuildAsync(default!, default);
    }

    /// <summary>
    /// A rebuild is derived bookkeeping. A caller who successfully wrote must not see an exception
    /// because a projection of it could not be recompiled — and the stale block is CLEARED, because
    /// absence degrades to the pre-feature behaviour while staleness manufactures knowledge-update
    /// errors.
    /// </summary>
    [Fact]
    public async Task RebuildAsync_WhenTheRebuildThrows_SwallowsItAndClearsTheBlock()
    {
        _workingMemory.RebuildAsync(Owner, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("neo4j is having a day"));

        var act = async () => await CreateSut().RebuildAsync(Owner, "a test", CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _workingMemory.Received(1).ClearAsync(Owner, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RebuildAsync_WhenClearOnRebuildFailureIsOff_LeavesTheBlockAlone()
    {
        _workingMemory.RebuildAsync(Owner, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = CreateSut(new WorkingMemoryOptions
        {
            Enabled = true,
            ClearOnRebuildFailure = false,
        });

        await sut.RebuildAsync(Owner, "a test", CancellationToken.None);

        await _workingMemory.DidNotReceiveWithAnyArgs().ClearAsync(default!, default);
    }

    /// <summary>
    /// The residual risk this design names and accepts: if the CLEAR also fails, a stale block can
    /// survive. It is logged at Error — nothing else can notice it — and never rethrown into a write
    /// that succeeded.
    /// </summary>
    [Fact]
    public async Task RebuildAsync_WhenTheClearAlsoThrows_StillDoesNotThrow()
    {
        _workingMemory.RebuildAsync(Owner, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rebuild failed"));
        _workingMemory.ClearAsync(Owner, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("clear failed too"));

        var act = async () => await CreateSut().RebuildAsync(Owner, "a test", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
