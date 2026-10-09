using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class Neo4jConsolidationServiceTests
{
    private readonly INeo4jTransactionRunner _tx = Substitute.For<INeo4jTransactionRunner>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGen = Substitute.For<IIdGenerator>();

    private Neo4jConsolidationService CreateSut()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        _idGen.GenerateId().Returns("run-1");
        return new Neo4jConsolidationService(_tx, _clock, _idGen, NullLogger<Neo4jConsolidationService>.Instance);
    }

    [Fact]
    public async Task DryRun_DoesNotMutate_AndDoesNotRecordAudit()
    {
        var report = await CreateSut().ConsolidateAsync(new ConsolidationOptions { DryRun = true });

        report.DryRun.Should().BeTrue();
        report.RunId.Should().Be("run-1");
        report.RanAtUtc.Should().Be(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));

        // A dry run only reads counts — never a write (mutation) or the audit-node write.
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_Mutates_AndRecordsAudit()
    {
        var report = await CreateSut().ConsolidateAsync(new ConsolidationOptions { DryRun = false });

        report.DryRun.Should().BeFalse();
        // Archive + remove-duplicate-preferences each run a mutating write.
        await _tx.Received().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        // The :ConsolidationRun audit node is written exactly once.
        await _tx.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisabledOperations_AreSkipped()
    {
        var opts = new ConsolidationOptions
        {
            DryRun = true,
            ArchiveExpiredConversations = false,
            RemoveDuplicatePreferences = false,
            DetectDuplicateEntities = false,
            DetectLongTraces = false,
        };

        await CreateSut().ConsolidateAsync(opts);

        // Nothing enabled → no reads at all.
        await _tx.DidNotReceive().ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
    }

    // ── Dreaming (AMDREAM001) ───────────────────────────────────────────

    private readonly IPreferenceSourceCheck _check = Substitute.For<IPreferenceSourceCheck>();

    private Neo4jConsolidationService CreateDreamingSut(bool withCheck = true)
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero));
        _idGen.GenerateId().Returns("run-1");
        return new Neo4jConsolidationService(_tx, _clock, _idGen, NullLogger<Neo4jConsolidationService>.Instance, withCheck ? _check : null);
    }

    private static ConsolidationOptions Dreaming(bool dryRun, bool entities = false, bool preferences = false, IReadOnlyCollection<string>? approved = null) => new()
    {
        DryRun = dryRun,
        ArchiveExpiredConversations = false,
        RemoveDuplicatePreferences = false,
        DetectDuplicateEntities = false,
        DetectLongTraces = false,
        CloseGenericEntities = entities,
        CloseUnsaidPreferences = preferences,
        ApprovedProposals = approved,
    };

    private void GivenABackpackStore()
    {
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<GenericEntityRule.EntityRow>>>>(), Arg.Any<CancellationToken>())
            .Returns([new("e1", "o1", "School backpack", "OBJECT"), new("e2", "o1", "New school backpack", "OBJECT")]);
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<(string?, string)>>>>(), Arg.Any<CancellationToken>())
            .Returns([("o1", "Vasco | carries | his school backpack")]);
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<ConsolidationProposal>>>>(), Arg.Any<CancellationToken>())
            .Returns([new ConsolidationProposal("r1", "connection", "o1", "Vasco -[OWNS]-> School backpack", "a connection to a generic entity this run closes")]);
    }

    [Fact]
    public async Task Dreaming_is_off_by_default()
    {
        var options = new ConsolidationOptions();

        options.CloseGenericEntities.Should().BeFalse();
        options.CloseUnsaidPreferences.Should().BeFalse();
        var report = await CreateDreamingSut().ConsolidateAsync(options with
        {
            ArchiveExpiredConversations = false, RemoveDuplicatePreferences = false, DetectDuplicateEntities = false, DetectLongTraces = false,
        });
        report.Proposals.Should().BeEmpty();
        await _check.DidNotReceive().FindUnsaidAsync(Arg.Any<PreferenceSourceRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dry_run_proposes_the_generic_entity_a_fact_names_and_its_connection_and_closes_nothing()
    {
        GivenABackpackStore();

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: true, entities: true));

        report.Proposals.Select(p => (p.Id, p.Kind)).Should().Equal(("e1", "entity"), ("r1", "connection"));
        report.Proposals[0].Text.Should().Be("School backpack (OBJECT)");
        report.GenericEntitiesClosed.Should().Be(1);
        report.GenericConnectionsClosed.Should().Be(1);
        report.TotalChanges.Should().Be(2);
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        await _tx.DidNotReceive().WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_apply_run_closes_and_records_the_run()
    {
        GivenABackpackStore();
        _tx.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>()).Returns(1);

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: false, entities: true));

        report.GenericEntitiesClosed.Should().Be(1);
        report.GenericConnectionsClosed.Should().Be(1);
        await _tx.Received(2).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
        await _tx.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_apply_run_with_approved_proposals_closes_only_those()
    {
        GivenABackpackStore();
        _tx.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>()).Returns(1);

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: false, entities: true, approved: ["r1"]));

        report.Proposals.Select(p => p.Id).Should().Equal("r1");
        report.GenericEntitiesClosed.Should().Be(0);
        report.GenericConnectionsClosed.Should().Be(1);
        await _tx.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unsaid_preferences_are_asked_once_per_source_message_and_only_a_guess_is_closed()
    {
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Neo4jConsolidationService.PreferenceSourceRow>>>>(), Arg.Any<CancellationToken>())
            .Returns([
                new("p1", "o1", "food", "loves octopus", "m1", "Helena's octopus got me eating octopus!", "2027-09-15T10:00:00Z", ["We went to Helena's."]),
                new("p2", "o1", "values", "values family traditions", "m1", "Helena's octopus got me eating octopus!", "2027-09-15T10:00:00Z", ["We went to Helena's."]),
            ]);
        _check.FindUnsaidAsync(Arg.Any<PreferenceSourceRequest>(), Arg.Any<CancellationToken>()).Returns(["p2"]);

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: true, preferences: true));

        report.Proposals.Should().ContainSingle().Which.Should().Be(new ConsolidationProposal(
            "p2", "preference", "o1", "values family traditions (values)", "the message it came from does not say it"));
        report.UnsaidPreferencesClosed.Should().Be(1);
        await _check.Received(1).FindUnsaidAsync(
            Arg.Is<PreferenceSourceRequest>(r => r.Preferences.Count == 2 && r.Earlier.Single() == "We went to Helena's."
                && r.SaidAt == new DateTimeOffset(2027, 9, 15, 10, 0, 0, TimeSpan.Zero)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_source_check_closes_nothing()
    {
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Neo4jConsolidationService.PreferenceSourceRow>>>>(), Arg.Any<CancellationToken>())
            .Returns([new("p1", "o1", "food", "loves octopus", "m1", "I ate octopus.", null, [])]);
        _check.FindUnsaidAsync(Arg.Any<PreferenceSourceRequest>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyCollection<string>>(_ => throw new HttpRequestException("402"));

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: true, preferences: true));

        report.Proposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_source_check_unsaid_preferences_are_skipped()
    {
        var report = await CreateDreamingSut(withCheck: false).ConsolidateAsync(Dreaming(dryRun: true, preferences: true));

        report.Proposals.Should().BeEmpty();
        await _tx.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<List<Neo4jConsolidationService.PreferenceSourceRow>>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_approved_preference_is_closed_without_asking_the_model_again()
    {
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<ConsolidationProposal>>>>(), Arg.Any<CancellationToken>())
            .Returns([new ConsolidationProposal("p2", "preference", "o1", "values family traditions (values)", "approved from a dry run: the message it came from does not say it")]);
        _tx.WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>()).Returns(1);

        var report = await CreateDreamingSut().ConsolidateAsync(Dreaming(dryRun: false, preferences: true, approved: ["p2"]));

        report.UnsaidPreferencesClosed.Should().Be(1);
        await _check.DidNotReceive().FindUnsaidAsync(Arg.Any<PreferenceSourceRequest>(), Arg.Any<CancellationToken>());
    }
}
