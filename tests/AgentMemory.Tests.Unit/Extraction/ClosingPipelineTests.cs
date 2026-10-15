using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class ClosingPipelineTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relationships = Substitute.For<IRelationshipRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private static readonly MemoryScope Scope = MemoryScope.For("owner", includeShared: false);
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(5);

    [Theory]
    [InlineData(false, false, FactClosureReason.Change)]
    [InlineData(false, true, FactClosureReason.Correction)]
    [InlineData(true, false, FactClosureReason.Correction)]
    [InlineData(true, true, FactClosureReason.Change)]
    [InlineData(true, true, FactClosureReason.Correction)]
    public async Task Fact_uses_exact_temporal_overload_and_records_only_success(bool temporal, bool accepted, FactClosureReason reason)
    {
        using var stop = new CancellationTokenSource();
        _facts.SupersedeAsync("old", "new", Scope, stop.Token).Returns(accepted);
        _facts.SupersedeAsync("old", "new", reason, At, Scope, stop.Token).Returns(accepted);
        var journal = BeginJournal();
        var result = await Pipeline(temporal).ApplyAsync(
            ClosingProposal.Fact("old", "new", reason, At, Scope, ClosingSource.DeterministicReplacement), stop.Token);
        result.Should().Be(accepted);
        if (temporal)
        {
            await _facts.Received(1).SupersedeAsync("old", "new", reason, At, Scope, stop.Token);
            await _facts.DidNotReceive().SupersedeAsync("old", "new", Scope, stop.Token);
        }
        else
        {
            await _facts.Received(1).SupersedeAsync("old", "new", Scope, stop.Token);
            await _facts.DidNotReceive().SupersedeAsync("old", "new", reason, At, Scope, stop.Token);
        }
        journal.Should().HaveCount(accepted ? 1 : 0);
        if (accepted) journal.Single().Should().Be(("new", "old"));
        _preferences.ReceivedCalls().Should().BeEmpty();
        _relationships.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData((int)ClosingSource.DeterministicReplacement, false)]
    [InlineData((int)ClosingSource.ExtractorCorrection, false)]
    [InlineData((int)ClosingSource.WithinExtractionReplacement, false)]
    [InlineData((int)ClosingSource.SimilarityJudge, true)]
    [InlineData((int)ClosingSource.WriterNamed, true)]
    public async Task Preference_preserves_source_outcome_attribution(int source, bool recorded)
    {
        using var stop = new CancellationTokenSource();
        _preferences.SupersedeAsync("old", "new", Scope, stop.Token).Returns(true);
        var journal = BeginJournal();
        (await Pipeline().ApplyAsync(ClosingProposal.Preference("old", "new", Scope, (ClosingSource)source), stop.Token)).Should().BeTrue();
        await _preferences.Received(1).SupersedeAsync("old", "new", Scope, stop.Token);
        journal.Should().HaveCount(recorded ? 1 : 0);
        _facts.ReceivedCalls().Should().BeEmpty();
        _relationships.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData((int)ClosingSource.DeterministicReplacement, "new", false)]
    [InlineData((int)ClosingSource.MirroredEdge, null, false)]
    [InlineData((int)ClosingSource.WriterNamed, "new", true)]
    public async Task Edge_keeps_the_supplied_time_without_inventing_a_successor(int source, string? successor, bool recorded)
    {
        using var stop = new CancellationTokenSource();
        _relationships.EndAsync("old", At, Scope, stop.Token).Returns(true);
        var journal = BeginJournal();
        (await Pipeline().ApplyAsync(ClosingProposal.Relationship("old", At, Scope, (ClosingSource)source, successor), stop.Token)).Should().BeTrue();
        await _relationships.Received(1).EndAsync("old", At, Scope, stop.Token);
        journal.Should().HaveCount(recorded ? 1 : 0);
        _facts.ReceivedCalls().Should().BeEmpty();
        _preferences.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Declined_preference_or_edge_is_not_recorded(bool edge)
    {
        var journal = BeginJournal();
        ClosingProposal proposal = edge
            ? ClosingProposal.Relationship("old", At, Scope, ClosingSource.WriterNamed, "new")
            : ClosingProposal.Preference("old", "new", Scope, ClosingSource.WriterNamed);
        (await Pipeline().ApplyAsync(proposal)).Should().BeFalse();
        journal.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_failure_or_cancellation_propagates_without_recording(bool cancelled)
    {
        using var stop = new CancellationTokenSource();
        Exception failure = cancelled ? new OperationCanceledException(stop.Token) : new InvalidOperationException("store declined the operation");
        _preferences.SupersedeAsync("old", "new", Scope, stop.Token).Returns<bool>(_ => throw failure);
        var journal = BeginJournal();
        var caught = await Record.ExceptionAsync(() => Pipeline().ApplyAsync(
            ClosingProposal.Preference("old", "new", Scope, ClosingSource.WriterNamed), stop.Token));
        caught.Should().BeSameAs(failure);
        journal.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeated_proposals_remain_the_sources_responsibility_and_replay_can_retry()
    {
        _facts.SupersedeAsync("old", "new", Scope, default).Returns(false, true);
        var journal = BeginJournal();
        var pipeline = Pipeline();
        var proposal = ClosingProposal.Fact("old", "new", FactClosureReason.Change, At, Scope, ClosingSource.ExtractorCorrection);
        (await pipeline.ApplyAsync(proposal)).Should().BeFalse();
        (await pipeline.ApplyAsync(proposal)).Should().BeTrue();
        await _facts.Received(2).SupersedeAsync("old", "new", Scope, default);
        journal.Should().Equal(("new", "old"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Withdrawal_preserves_its_repository_result_without_a_successor_or_journal_entry(bool entity, bool accepted)
    {
        using var stop = new CancellationTokenSource();
        _facts.InvalidateAsync("old", Scope, stop.Token).Returns(accepted);
        _entities.InvalidateAsync("old", Scope, stop.Token).Returns(accepted);
        var journal = BeginJournal();
        ClosingProposal proposal = entity
            ? ClosingProposal.RetireEntity("old", Scope, ClosingSource.CorrectedName)
            : ClosingProposal.WithdrawFact("old", Scope, ClosingSource.WithinExtractionReplacement);
        (await Pipeline(temporal: true).ApplyAsync(proposal, stop.Token)).Should().Be(accepted);
        if (entity)
        {
            await _entities.Received(1).InvalidateAsync("old", Scope, stop.Token);
            _facts.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            await _facts.Received(1).InvalidateAsync("old", Scope, stop.Token);
            _entities.ReceivedCalls().Should().BeEmpty();
        }
        _preferences.ReceivedCalls().Should().BeEmpty();
        _relationships.ReceivedCalls().Should().BeEmpty();
        journal.Should().BeEmpty();
    }

    private ClosingPipeline Pipeline(bool temporal = false) => new(_facts, _preferences, _relationships, _entities,
        new ExtractionOptions { BitemporalChanges = temporal });

    private static List<(string Winner, string Loser)> BeginJournal()
    {
        var journal = new List<(string Winner, string Loser)>();
        PersistenceClosingJournal.Begin(journal);
        return journal;
    }
}
