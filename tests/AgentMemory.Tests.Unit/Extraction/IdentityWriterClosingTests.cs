using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class IdentityWriterClosingTests
{
    private sealed class Fixture
    {
        public readonly IFactRepository Facts = Substitute.For<IFactRepository>();
        public readonly IMemoryUpdateJudge Judge = Substitute.For<IMemoryUpdateJudge>();
        public readonly List<bool> ModelTransactionStates = [];
        public readonly PersistenceStage Stage;
        public bool InTransaction, FailCommit;
        public Fixture()
        {
            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
            var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
            var candidates = Substitute.For<IIdentityCandidateProvider>();
            candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true });
            var store = Substitute.For<IIdentityDecisionRepository>(); store.IsEnabled.Returns(true); store.SupportsAtomicIngestion.Returns(true);
            store.ExecuteScopedAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<Func<CancellationToken, Task<PersistenceResult>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Atomic(call.Arg<Func<CancellationToken, Task<PersistenceResult>>>(), call.Arg<CancellationToken>()));
            var identity = new IdentityIngestion(IdentityConfiguration.Capture(new() { Enabled = true }), TimeProvider.System, candidates, store, embeddings, ids);
            Facts.UpsertAsync(Arg.Any<Fact>(), Arg.Any<CancellationToken>()).Returns(call =>
                call.Arg<Fact>() with { FactId = call.Arg<Fact>().Subject == "Acme go" ? "new1" : "new2" });
            Facts.GetByIdAsync("old1", Arg.Any<CancellationToken>()).Returns(new Fact
            { FactId = "old1", Subject = "Acme go", Predicate = "live", Object = "later", OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
            Facts.GetByIdAsync("old2", Arg.Any<CancellationToken>()).Returns(new Fact
            { FactId = "old2", Subject = "Acme", Predicate = "go live", Object = "later", OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
            Facts.SupersedeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(true);
            Judge.IsEnabled.Returns(true);
            Judge.JudgeAsync(Arg.Any<MemoryUpdateRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                ModelTransactionStates.Add(InTransaction);
                return (IReadOnlyDictionary<string, double>)call.Arg<MemoryUpdateRequest>().Pairs.ToDictionary(pair => pair.Key, _ => 1.0);
            });
            Stage = new PersistenceStage(embeddings, Substitute.For<IEntityRepository>(), Facts,
                Substitute.For<IPreferenceRepository>(), Substitute.For<IRelationshipRepository>(), TimeProvider.System, ids,
                NullLogger<PersistenceStage>.Instance, new PassThroughMemoryPersistenceTransaction(),
                Options.Create(new ExtractionOptions { UseBatchEmbeddingRequests = false }), updateJudge: Judge, identity: identity);
        }
        private async Task<PersistenceResult> Atomic(Func<CancellationToken, Task<PersistenceResult>> body, CancellationToken cancellationToken)
        {
            InTransaction = true;
            try { var result = await body(cancellationToken); if (FailCommit) throw new IOException("injected missing commit acknowledgement"); return result; }
            finally { InTransaction = false; }
        }
        public Task<PersistenceResult> Persist(bool named = true) => Stage.PersistAsync(new()
        {
            SourceMessageIds = ["root"], SourceText = "The two plans changed.", WrittenByWriter = true,
            FilteredFacts = [new() { Subject = "Acme go", Predicate = "live", Object = "soon", Confidence = 1, ReplacesId = named ? "old1" : null },
                new() { Subject = "Acme", Predicate = "go live", Object = "soon", Confidence = 1, ReplacesId = named ? "old2" : null }]
        }, "pablito");
    }
    [Fact]
    public async Task Space_joined_display_keys_do_not_assign_the_wrong_writer_closing_winner()
    {
        var f = new Fixture(); await f.Persist();
        await f.Facts.Received(1).SupersedeAsync("old1", "new1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await f.Facts.Received(1).SupersedeAsync("old2", "new2", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await f.Facts.DidNotReceive().SupersedeAsync("old2", "new1", Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Existing_writer_closing_models_do_not_hold_the_identity_transaction()
    {
        var f = new Fixture(); await f.Persist();
        f.ModelTransactionStates.Should().ContainSingle().Which.Should().BeFalse();
    }
    [Fact]
    public async Task A_commit_without_acknowledgement_does_not_start_post_commit_model_work()
    {
        var f = new Fixture { FailCommit = true }; var action = () => f.Persist();
        await action.Should().ThrowAsync<MemoryIngestionException>(); f.ModelTransactionStates.Should().BeEmpty();
    }
    [Fact]
    public async Task Without_named_closings_the_existing_judge_is_not_called()
    {
        var f = new Fixture(); await f.Persist(named: false); f.ModelTransactionStates.Should().BeEmpty();
    }
}
