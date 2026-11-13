using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;
using System.Text.Json;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class IdentityDecisionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jIdentityDecisionRepository _decisions;
    private readonly Neo4jIdentityEvidenceRepository _evidence;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jFactRepository _facts;
    private readonly IOptions<Neo4jOptions> _options;

    public IdentityDecisionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var options = new Neo4jOptions(); options.Extensions.Add("identity-evidence"); _options = Options.Create(options);
        _decisions = new(fixture.TransactionRunner, _options); _evidence = new(fixture.TransactionRunner, _options);
        _entities = new(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _facts = new(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }
    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Schema/Migrations/ext/identity-evidence"), "*.cypher").Order(StringComparer.Ordinal))
            foreach (var statement in (await File.ReadAllTextAsync(path)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await _fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(statement); });
    }
    public Task DisposeAsync() => Task.CompletedTask;
    private static string Id() => Guid.NewGuid().ToString("N");
    private static VerifiedIdentityKey Key(string id = "42") => new()
    { Namespace = "staff", Authority = "directory", SourceId = "staff:" + id, Values = new Dictionary<string, string> { ["employee_id"] = id } };
    private static IdentityDecisionWrite Input(string name = "Pepe", string? owner = "pablito", string source = "message")
    {
        var observationId = Id();
        return new() { Decision = new()
        {
            DecisionId = Id(), ObservationKey = Id(), Status = IdentityDecisionKind.New, Policy = new(),
            Observation = new() { EntityId = observationId, OwnerId = owner, Name = name, Type = "PERSON", Confidence = 1,
                SourceMessageIds = [source], CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new() { SourceMessageIds = [source], Sources = [new() { SourceId = source, Text = name + " is arriving.", OriginIds = [source] }] },
            CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch
        } };
    }
    private Task<Entity> Seed(string name = "José García", string? owner = "pablito") => _entities.UpsertAsync(new()
    { EntityId = Id(), OwnerId = owner, Name = name, Type = "PERSON", Description = "Directory card", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch });
    private Task<Fact> Fact(string subject = "Pepe", string obj = "arriving", string? owner = "pablito", params string[] sources) => _facts.UpsertAsync(new()
    { FactId = Id(), OwnerId = owner, Subject = subject, Predicate = "mentions", Object = obj, Confidence = 1,
        SourceMessageIds = sources.Length == 0 ? ["message"] : sources, CreatedAtUtc = DateTimeOffset.UnixEpoch });
    private async Task<IdentityDecisionWrite> Confirmed(string? owner = "pablito", string? targetOwner = "pablito", string source = "message")
    {
        var target = await Seed(owner: targetOwner); await _evidence.AssertKeyAsync(target.EntityId, Key(), ["employee_id"], targetOwner);
        var candidates = await _evidence.FindExactCandidatesAsync(new()
        { Name = "Pepe", Type = "PERSON", OwnerId = owner, IncludeShared = targetOwner is null, Limit = 12, VerifiedKeys = [Key()], IdentityProperties = ["employee_id"] });
        var input = Input(owner: owner, source: source);
        return input with { ExpectedCandidates = candidates.Candidates, IncludeShared = targetOwner is null, Decision = input.Decision with
        { Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = target.EntityId,
            Policy = new() { IdentityProperties = ["employee_id"] }, Context = input.Decision.Context with { VerifiedKeys = [Key()] } } };
    }
    private async Task<long> Count(string cypher, object? parameters = null) => await _fixture.TransactionRunner.ReadAsync(async runner =>
    { var cursor = await runner.RunAsync(cypher, parameters); return global::Neo4j.Driver.ValueExtensions.As<long>((await cursor.SingleAsync())["count"]); });

    [Theory]
    [InlineData(IdentityDecisionKind.New, IdentityFactRole.Subject)]
    [InlineData(IdentityDecisionKind.New, IdentityFactRole.Object)]
    [InlineData(IdentityDecisionKind.Pending, IdentityFactRole.Subject)]
    [InlineData(IdentityDecisionKind.Pending, IdentityFactRole.Object)]
    [InlineData(IdentityDecisionKind.Confirmed, IdentityFactRole.Subject)]
    [InlineData(IdentityDecisionKind.Confirmed, IdentityFactRole.Object)]
    public async Task All_initial_statuses_and_roles_preserve_words_and_own_their_support(IdentityDecisionKind status, IdentityFactRole role)
    {
        var input = status == IdentityDecisionKind.Confirmed ? await Confirmed() : Input();
        if (status == IdentityDecisionKind.Pending)
        {
            var target = await Seed("Pepe");
            var candidates = await _evidence.FindExactCandidatesAsync(new() { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12 });
            input = input with { ExpectedCandidates = candidates.Candidates, Decision = input.Decision with { Status = status, PendingEntityIds = [target.EntityId] } };
        }
        var fact = await Fact(role == IdentityFactRole.Subject ? "Pepe" : "speaker", role == IdentityFactRole.Object ? "Pepe" : "arriving");
        input = input with { Bindings = [new() { FactId = fact.FactId, Role = role }] };
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(status); saved.Observation.Name.Should().Be("Pepe");
        (await _decisions.GetAsync(saved.DecisionId, "pablito")).Should().BeEquivalentTo(saved);
        var targetId = saved.ConfirmedEntityId ?? saved.Observation.EntityId;
        (await Count("MATCH (:Fact {id: $fact})-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id: $target}) RETURN count(b) AS count",
            new { fact = fact.FactId, target = targetId })).Should().Be(1);
        (await Count("MATCH ()-[r:SAME_AS|ABOUT]->() RETURN count(r) AS count")).Should().Be(0);
        var stored = await _facts.GetByIdAsync(fact.FactId); stored!.Subject.Should().Be(fact.Subject); stored.Object.Should().Be(fact.Object);
        (await Count("MATCH (:IdentityDecision)-[:HAS_IDENTITY_REVISION]->(r:IdentityDecisionRevision) RETURN count(r) AS count")).Should().Be(1);
        if (status == IdentityDecisionKind.Confirmed) (await _entities.GetByIdAsync(saved.Observation.EntityId)).Should().BeNull();
        if (status == IdentityDecisionKind.Pending) (await Count("MATCH ()-[r:POSSIBLE_SAME_AS]->() RETURN count(r) AS count")).Should().Be(1);
    }

    [Fact]
    public async Task Confirmation_does_not_rewrite_the_existing_targets_name_owner_or_provenance()
    {
        var input = await Confirmed(); var before = await _entities.GetByIdAsync(input.Decision.ConfirmedEntityId!);
        await _decisions.SaveAsync(input, "pablito");
        (await _entities.GetByIdAsync(input.Decision.ConfirmedEntityId!)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Replaying_and_restarting_a_decision_is_idempotent_including_both_roles_of_a_deduplicated_fact()
    {
        var firstFact = await Fact("Pepe", "Pepe"); var repeatedFact = await Fact("Pepe", "Pepe"); repeatedFact.FactId.Should().Be(firstFact.FactId);
        var input = await Confirmed(); input = input with { Bindings = [new() { FactId = repeatedFact.FactId, Role = IdentityFactRole.Subject }, new() { FactId = repeatedFact.FactId, Role = IdentityFactRole.Object }] };
        await _decisions.SaveAsync(input, "pablito");
        var restarted = new Neo4jIdentityDecisionRepository(_fixture.TransactionRunner, _options);
        var replay = await restarted.SaveAsync(input, "pablito"); replay.Version.Should().Be(1);
        (await Count("MATCH (d:IdentityDecision) RETURN count(d) AS count")).Should().Be(1);
        (await Count("MATCH (b:IdentityFactBinding) RETURN count(b) AS count")).Should().Be(2);
        (await Count("MATCH (r:IdentityDecisionRevision) RETURN count(r) AS count")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_identical_ingestions_create_one_decision_revision_and_binding()
    {
        var fact = await Fact(); var input = await Confirmed(); input = input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] };
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new Neo4jIdentityDecisionRepository(_fixture.TransactionRunner, _options).SaveAsync(input, "pablito")));
        results.Select(result => result.DecisionId).Distinct().Should().ContainSingle();
        (await Count("MATCH (d:IdentityDecision) RETURN count(d) AS count")).Should().Be(1);
        (await Count("MATCH (b:IdentityFactBinding) RETURN count(b) AS count")).Should().Be(1);
        (await Count("MATCH (r:IdentityDecisionRevision) RETURN count(r) AS count")).Should().Be(1);
    }

    [Fact]
    public async Task Independent_decisions_can_support_the_same_fact_without_sharing_binding_ownership()
    {
        var input = await Confirmed(source: "m1"); var fact = await Fact(sources: ["m1", "m2"]);
        input = input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] };
        await _decisions.SaveAsync(input, "pablito");
        var next = Input(source: "m2"); next = next with { ExpectedCandidates = input.ExpectedCandidates, Bindings = input.Bindings, Decision = next.Decision with
        { Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = input.Decision.ConfirmedEntityId, Policy = input.Decision.Policy, Context = next.Decision.Context with { VerifiedKeys = [Key()] } } };
        await _decisions.SaveAsync(next, "pablito");
        (await Count("MATCH (:Fact {id:$id})-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding) RETURN count(b) AS count", new { id = fact.FactId })).Should().Be(2);
        (await Count("MATCH (d:IdentityDecision) RETURN count(d) AS count")).Should().Be(2);
    }

    [Fact]
    public async Task Late_competing_key_cannot_be_hidden_by_the_planned_candidate_snapshot()
    {
        var input = await Confirmed(); var competitor = await Seed("another employee");
        await _evidence.AssertKeyAsync(competitor.EntityId, Key(), ["employee_id"], "pablito");
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(IdentityDecisionKind.Pending); saved.ConfirmedEntityId.Should().BeNull();
        saved.PendingEntityIds.Should().BeEquivalentTo(input.Decision.ConfirmedEntityId, competitor.EntityId);
    }

    [Fact]
    public async Task Late_key_contradiction_or_target_content_change_cannot_confirm_a_stale_plan()
    {
        var input = await Confirmed(); await _evidence.AssertKeyAsync(input.Decision.ConfirmedEntityId!, Key("99"), ["employee_id"], "pablito");
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.New);
    }

    [Theory]
    [InlineData(null)] [InlineData("*")]
    public async Task Exact_owner_scope_and_shared_target_visibility_are_preserved(string? targetOwner)
    {
        var input = await Confirmed(owner: targetOwner, targetOwner: targetOwner);
        var saved = await _decisions.SaveAsync(input, targetOwner);
        saved.Status.Should().Be(IdentityDecisionKind.Confirmed);
        (await _decisions.GetAsync(saved.DecisionId, "other")).Should().BeNull();
    }

    [Fact]
    public async Task A_private_decision_can_reference_an_explicitly_included_shared_target()
    {
        var input = await Confirmed(targetOwner: null);
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public async Task An_invalid_second_binding_rolls_back_the_first_binding_decision_revision_and_observation()
    {
        var fact = await Fact(); var input = Input() with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }, new() { FactId = "missing", Role = IdentityFactRole.Object }] };
        var action = () => _decisions.SaveAsync(input, "pablito"); await action.Should().ThrowAsync<InvalidOperationException>();
        (await Count("MATCH (n) WHERE n:IdentityDecision OR n:IdentityFactBinding OR n:IdentityDecisionRevision OR n:Entity RETURN count(n) AS count")).Should().Be(0);
        (await _facts.GetByIdAsync(fact.FactId)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_idempotent_replay_cannot_extend_confirmation_after_a_competing_key_appears()
    {
        var input = await Confirmed(); var first = await Fact();
        input = input with { Bindings = [new() { FactId = first.FactId, Role = IdentityFactRole.Subject }] };
        await _decisions.SaveAsync(input, "pablito");
        var competitor = await Seed("another employee"); await _evidence.AssertKeyAsync(competitor.EntityId, Key(), ["employee_id"], "pablito");
        var later = await Fact(obj: "later detail");
        var action = () => _decisions.SaveAsync(input with { Bindings = [new() { FactId = later.FactId, Role = IdentityFactRole.Subject }] }, "pablito");
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await Count("MATCH (:Fact {id:$id})-[:HAS_IDENTITY_BINDING]->(b) RETURN count(b) AS count", new { id = later.FactId })).Should().Be(0);
        (await Count("MATCH (:Fact {id:$id})-[:HAS_IDENTITY_BINDING]->(b) RETURN count(b) AS count", new { id = first.FactId })).Should().Be(1);
    }

    [Fact]
    public async Task A_separate_observation_cannot_reuse_an_existing_entity_id_without_authority()
    {
        var existing = await Seed("Pepe"); var input = Input();
        input = input with { Decision = input.Decision with { Observation = input.Decision.Observation with { EntityId = existing.EntityId } } };
        var action = () => _decisions.SaveAsync(input, "pablito"); await action.Should().ThrowAsync<InvalidOperationException>();
        (await _entities.GetByIdAsync(existing.EntityId)).Should().BeEquivalentTo(existing);
        (await Count("MATCH (d:IdentityDecision) RETURN count(d) AS count")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Shared_and_literal_star_owner_facts_must_not_share_a_deduplication_identity(bool sharedFirst)
    {
        var firstOwner = sharedFirst ? null : "*"; var secondOwner = sharedFirst ? "*" : null;
        var first = await Fact(owner: firstOwner); var second = await Fact(owner: secondOwner);
        second.FactId.Should().NotBe(first.FactId, "shared knowledge and a private owner named '*' are different scopes");
        first.OwnerId.Should().Be(firstOwner); second.OwnerId.Should().Be(secondOwner);
    }

    [Fact]
    public async Task An_immutable_receipt_records_the_evidence_actually_revalidated_by_the_store()
    {
        var input = await Confirmed(); var saved = await _decisions.SaveAsync(input, "pablito");
        using var receipt = JsonDocument.Parse(saved.EvidenceJson);
        var candidates = receipt.RootElement.GetProperty("candidates");
        candidates.GetArrayLength().Should().Be(1);
        candidates[0].GetProperty("id").GetString().Should().Be(saved.ConfirmedEntityId);
        candidates[0].GetProperty("verifiedKeys")[0].GetProperty("Authority").GetString().Should().Be("directory");
        candidates[0].GetProperty("verifiedKeys")[0].GetProperty("Values").GetProperty("employee_id").GetString().Should().Be("42");
        receipt.RootElement.GetProperty("persistedDecision").GetString().Should().Be("Confirmed");
    }

    [Fact]
    public async Task A_lost_commit_acknowledgement_can_be_retried_without_duplicate_decisions_or_support()
    {
        var fact = await Fact(); var input = await Confirmed(); input = input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] };
        var ambiguous = new Neo4jIdentityDecisionRepository(new LostAcknowledgementRunner(_fixture.TransactionRunner), _options);
        var action = () => ambiguous.SaveAsync(input, "pablito"); await action.Should().ThrowAsync<IOException>();
        (await _decisions.GetAsync(input.Decision.DecisionId, "pablito")).Should().NotBeNull();
        (await _decisions.SaveAsync(input, "pablito")).Version.Should().Be(1);
        (await Count("MATCH (d:IdentityDecision) RETURN count(d) AS count")).Should().Be(1);
        (await Count("MATCH (b:IdentityFactBinding) RETURN count(b) AS count")).Should().Be(1);
        (await Count("MATCH (r:IdentityDecisionRevision) RETURN count(r) AS count")).Should().Be(1);
    }

    private sealed class LostAcknowledgementRunner(INeo4jTransactionRunner inner) : INeo4jTransactionRunner
    {
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken ct = default) => inner.ReadAsync(work, ct);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken ct = default) => inner.ReadAsync(work, ct);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken ct = default) => inner.WriteAsync(work, ct);
        public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken ct = default)
        { await inner.WriteAsync(work, ct); throw new IOException("injected lost acknowledgement after the server committed"); }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(0)]
    public async Task Failure_before_each_statement_or_commit_leaves_no_partial_identity_state(int failAt)
    {
        var fact = await Fact(); var input = Input() with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] };
        var failing = new FaultRunner(_fixture.TransactionRunner, failAt);
        var repository = new Neo4jIdentityDecisionRepository(failing, _options);
        var action = () => repository.SaveAsync(input, "pablito"); await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected*");
        (await Count("MATCH (n) WHERE n:IdentityDecision OR n:IdentityFactBinding OR n:IdentityDecisionRevision OR n:Entity RETURN count(n) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task Reasserting_identical_key_keeps_a_confirmed_decision_replayable()
    {
        var input = await Confirmed();
        var saved = await _decisions.SaveAsync(input, "pablito");
        var before = input.ExpectedCandidates.Single().EvidenceVersion;
        await _evidence.AssertKeyAsync(saved.ConfirmedEntityId!, Key(), ["employee_id"], "pablito");
        var replayed = await _decisions.SaveAsync(input, "pablito");
        replayed.Should().BeEquivalentTo(saved);
        var current = await _evidence.FindExactCandidatesAsync(new() { Name = "Pepe", Type = "PERSON",
            OwnerId = "pablito", Limit = 12, VerifiedKeys = [Key()], IdentityProperties = ["employee_id"] });
        current.Candidates.Should().ContainSingle().Which.EvidenceVersion.Should().Be(before,
            "a content-idempotent assertion must not create a new evidence revision");
    }

    [Fact]
    public async Task A_genuinely_new_verified_key_still_invalidates_confirmed_replay()
    {
        var input = await Confirmed(); var saved = await _decisions.SaveAsync(input, "pablito");
        await _evidence.AssertKeyAsync(saved.ConfirmedEntityId!, Key("43"), ["employee_id"], "pablito");
        var action = () => _decisions.SaveAsync(input, "pablito");
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*target changed*");
        (await _decisions.GetAsync(saved.DecisionId, "pablito")).Should().BeEquivalentTo(saved);
    }

    private sealed class FaultRunner(INeo4jTransactionRunner inner, int failAt) : INeo4jTransactionRunner
    {
        public int StatementCount { get; private set; }
        public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken ct = default) => inner.ReadAsync(work, ct);
        public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken ct = default) => inner.ReadAsync(work, ct);
        public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken ct = default) => inner.WriteAsync(work, ct);
        public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken ct = default) => inner.WriteAsync(async actual =>
        {
            var count = 0; var proxy = Substitute.For<IAsyncQueryRunner>();
            Task<IResultCursor> Run(string query, object parameters)
            { StatementCount=++count; if (count == failAt) throw new InvalidOperationException("injected statement failure"); return actual.RunAsync(query, parameters); }
            proxy.RunAsync(Arg.Any<string>(), Arg.Any<object>()).Returns(call => Run(call.Arg<string>(), call.ArgAt<object>(1)));
            proxy.RunAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object?>>()).Returns(call => Run(call.Arg<string>(), call.ArgAt<object>(1)));
            var result = await work(proxy);
            if (failAt == 0) throw new InvalidOperationException("injected failure before commit");
            return result;
        }, ct);
    }
    private async Task<IdentityDecisionWrite> ContextualInput(bool allow = true)
    {
        var target = await Seed("Pepe");
        var candidates = await _evidence.FindExactCandidatesAsync(new()
        { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12 });
        var input = Input();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync("CREATE (:Conversation {id:'context-conversation', user_id:'pablito'}), " +
                "(:Message {id:'message', conversation_id:'context-conversation', content:'Pepe is arriving.'})");
        });
        return input with { ExpectedCandidates = candidates.Candidates, Decision = input.Decision with
        {
            Status = allow ? IdentityDecisionKind.Confirmed : IdentityDecisionKind.Pending,
            ConfirmedEntityId = allow ? target.EntityId : null, PendingEntityIds = allow ? [] : [target.EntityId],
            Policy = new() { AllowContextualConfirmation = allow },
            ContextualEvidence = new() { Verdict = IdentityAdjudicationVerdict.Same, CandidateId = target.EntityId,
                Citations = [new() { SourceId = "message", Quote = "Pepe is arriving." }] }
        } };
    }

    [Fact]
    public async Task Contextual_confirmation_revalidates_sources_and_target_and_survives_binding_replay()
    {
        var input = await ContextualInput(); var target = input.Decision.ConfirmedEntityId!;
        var original = await _entities.GetByIdAsync(target);
        var first = await _decisions.SaveAsync(input, "pablito");
        first.Status.Should().Be(IdentityDecisionKind.Confirmed); first.ConfirmedEntityId.Should().Be(target);
        first.ContextualEvidence.Should().BeEquivalentTo(input.Decision.ContextualEvidence);
        (await _decisions.GetAsync(first.DecisionId, "pablito"))!.ContextualEvidence.Should().BeEquivalentTo(first.ContextualEvidence);
        var fact = await Fact();
        var replay = await _decisions.SaveAsync(input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] }, "pablito");
        replay.Status.Should().Be(IdentityDecisionKind.Confirmed);
        (await _entities.GetByIdAsync(target)).Should().BeEquivalentTo(original);
        (await Count("MATCH (b:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(b) AS count", new { target })).Should().Be(1);
        (await Count("MATCH ()-[r:SAME_AS]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task Review_only_contextual_receipt_is_durable_without_conferring_identity()
    {
        var input = await ContextualInput(allow: false);
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(IdentityDecisionKind.Pending);
        (await _decisions.GetAsync(saved.DecisionId, "pablito"))!.ContextualEvidence.Should().BeEquivalentTo(input.Decision.ContextualEvidence);
    }

    [Theory]
    [InlineData("changed")] [InlineData("missing")] [InlineData("foreign")] [InlineData("invalidated")]
    public async Task Stale_missing_or_foreign_context_cannot_authorize_contextual_confirmation(string change)
    {
        var input = await ContextualInput();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            if (change == "changed") await runner.RunAsync("MATCH (m:Message {id:'message'}) SET m.content = 'Pepe is elsewhere.'");
            if (change == "missing") await runner.RunAsync("MATCH (m:Message {id:'message'}) DELETE m");
            if (change == "invalidated") await runner.RunAsync("MATCH (m:Message {id:'message'}) SET m.invalidated_at = datetime()");
            if (change == "foreign") await runner.RunAsync("CREATE (:Conversation {id:'foreign-conversation', user_id:'other'}), " +
                "(:Message {id:'foreign-context', conversation_id:'foreign-conversation', content:'Pepe is arriving.'})");
        });
        if (change == "foreign") input = input with { Decision = input.Decision with
        { Context = input.Decision.Context with { Sources = [new() { SourceId = "foreign-context", Text = "Pepe is arriving.", OriginIds = ["foreign-context"] }] },
            ContextualEvidence = input.Decision.ContextualEvidence! with { Citations = [new() { SourceId = "foreign-context", Quote = "Pepe is arriving." }] } } };
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public async Task A_hydrated_candidate_source_from_the_owner_history_supports_contextual_confirmation_without_becoming_provenance()
    {
        var input = await ContextualInput();
        const string prior = "Pepe Garcia leads the directory team.";
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("CREATE (:Message {id:'prior', conversation_id:'context-conversation', content:$prior})", new { prior }); });
        input = input with { Decision = input.Decision with
        {
            Context = input.Decision.Context with { Sources = [.. input.Decision.Context.Sources, new() { SourceId = "prior", Text = prior, OriginIds = ["prior"] }] },
            ContextualEvidence = input.Decision.ContextualEvidence! with
            { Citations = [.. input.Decision.ContextualEvidence!.Citations, new() { SourceId = "prior", Quote = "Pepe Garcia leads" }] }
        } };
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(IdentityDecisionKind.Confirmed);
        saved.Observation.SourceMessageIds.Should().Equal("message");
        saved.Context.SourceMessageIds.Should().Equal("message");
        saved.Context.Sources.Select(source => source.SourceId).Should().Equal("message", "prior");
    }

    [Fact]
    public async Task Every_offered_source_is_checked_even_when_the_model_does_not_quote_it()
    {
        var input = await ContextualInput();
        input = input with { Decision = input.Decision with { Context = input.Decision.Context with
        { Sources = [.. input.Decision.Context.Sources, new() { SourceId = "unpersisted-context", Text = "This is the same person.", OriginIds = ["unpersisted-context"] }] } } };
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public async Task A_changed_candidate_cannot_be_confirmed_from_an_old_contextual_result()
    {
        var input = await ContextualInput();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.description = 'Different directory card'", new { id = input.Decision.ConfirmedEntityId }); });
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public async Task Contextual_result_cannot_override_a_fresh_verified_key_contradiction()
    {
        var input = await ContextualInput(); var id = input.Decision.ConfirmedEntityId!;
        await _evidence.AssertKeyAsync(id, Key("42"), ["employee_id"], "pablito");
        input = input with { Decision = input.Decision with { Policy = new() { AllowContextualConfirmation = true, IdentityProperties = ["employee_id"] },
            Context = input.Decision.Context with { VerifiedKeys = [Key("43")] } } };
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public async Task A_confirmed_contextual_replay_refuses_changed_source_text_before_adding_support()
    {
        var input = await ContextualInput();
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.Confirmed);
        var fact = await Fact();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (m:Message {id:'message'}) SET m.content = 'Pepe is a different person.'"); });
        var save = () => _decisions.SaveAsync(input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] }, "pablito");
        await save.Should().ThrowAsync<InvalidOperationException>();
        (await Count("MATCH (b:IdentityFactBinding) RETURN count(b) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task Opaque_same_json_cannot_replace_a_typed_contextual_receipt()
    {
        var input = await ContextualInput();
        input = input with { Decision = input.Decision with { ContextualEvidence = null,
            EvidenceJson = "{\"decision\":\"same\",\"candidateId\":\"" + input.Decision.ConfirmedEntityId + "\"}" } };
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shared_context_sources_require_explicit_shared_permission(bool includeShared)
    {
        var input = await ContextualInput();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("CREATE (:Conversation {id:'shared-conversation'}), " +
            "(:Message {id:'shared-context', conversation_id:'shared-conversation', content:'This is the shared team directory.'})"); });
        input = input with { IncludeShared = includeShared, Decision = input.Decision with { Context = input.Decision.Context with
        { Sources = [.. input.Decision.Context.Sources, new() { SourceId = "shared-context", Text = "This is the shared team directory.", OriginIds = ["shared-context"] }] } } };
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(includeShared ? IdentityDecisionKind.Confirmed : IdentityDecisionKind.Pending);
    }

    [Fact]
    public async Task New_exact_key_ambiguity_cannot_be_overridden_by_the_contextual_proposal()
    {
        var input = await ContextualInput(); var target = input.Decision.ConfirmedEntityId!;
        await _evidence.AssertKeyAsync(target, Key(), ["employee_id"], "pablito");
        var before = await _evidence.FindExactCandidatesAsync(new()
        { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12, VerifiedKeys = [Key()], IdentityProperties = ["employee_id"] });
        input = input with { ExpectedCandidates = before.Candidates, Decision = input.Decision with
        { Policy = new() { AllowContextualConfirmation = true, IdentityProperties = ["employee_id"] },
            Context = input.Decision.Context with { VerifiedKeys = [Key()] } } };
        var competitor = await Seed("Another person");
        await _evidence.AssertKeyAsync(competitor.EntityId, Key(), ["employee_id"], "pablito");
        var saved = await _decisions.SaveAsync(input, "pablito");
        saved.Status.Should().Be(IdentityDecisionKind.Pending);
        saved.PendingEntityIds.Should().BeEquivalentTo([target, competitor.EntityId]);
    }

    [Fact]
    public async Task Changed_sibling_candidate_invalidates_the_contextual_comparison()
    {
        var input = await ContextualInput(); var sibling = await Seed("Pepe");
        var offered = await _evidence.FindExactCandidatesAsync(new() { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12 });
        input = input with { ExpectedCandidates = offered.Candidates };
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.description = 'The alternative candidate changed'", new { id = sibling.EntityId }); });
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public async Task A_new_exact_candidate_invalidates_the_contextual_comparison()
    {
        var input = await ContextualInput(); await Seed("Pepe");
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public async Task Contextual_replay_checks_the_other_offered_candidates_before_appending_support()
    {
        var input = await ContextualInput(); var sibling = await Seed("Pepe");
        var offered = await _evidence.FindExactCandidatesAsync(new() { Name = "Pepe", Type = "PERSON", OwnerId = "pablito", Limit = 12 });
        input = input with { ExpectedCandidates = offered.Candidates };
        (await _decisions.SaveAsync(input, "pablito")).Status.Should().Be(IdentityDecisionKind.Confirmed);
        var fact = await Fact();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.description = 'The alternative candidate changed'", new { id = sibling.EntityId }); });
        var replay = () => _decisions.SaveAsync(input with { Bindings = [new() { FactId = fact.FactId, Role = IdentityFactRole.Subject }] }, "pablito");
        await replay.Should().ThrowAsync<InvalidOperationException>();
        (await Count("MATCH (b:IdentityFactBinding) RETURN count(b) AS count")).Should().Be(0);
    }

    private async Task<IdentityRelationshipObservation> RelationshipObservationInput()
    {
        var source = await _decisions.SaveAsync(Input("Pepe"),"pablito");
        var target = await _decisions.SaveAsync(Input("Acme"),"pablito");
        return new() { ObservationId = "original-relation", OwnerId = "pablito", SourceMessageIds = ["message"],
            Observation = new() { SourceEntity = "Pepe", TargetEntity = "Acme", RelationshipType = "WORKS_FOR" },
            SourceDecisionId = source.DecisionId, TargetDecisionId = target.DecisionId, CreatedAtUtc = DateTimeOffset.UnixEpoch };
    }

    [Fact]
    public async Task Original_relationship_backend_retains_words_roles_and_stable_replay_without_ordinary_edges()
    {
        var input = await RelationshipObservationInput();
        var saved = await _decisions.SaveRelationshipObservationAsync(input,"pablito");
        saved.Should().BeEquivalentTo(input);
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito")).Should().BeEquivalentTo(saved);
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"other")).Should().BeNull();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId)).Should().BeNull();
        var replay = await _decisions.SaveRelationshipObservationAsync(input with { SourceDecisionId = null,
            CreatedAtUtc = DateTimeOffset.UtcNow, Observation = input.Observation with { Confidence = .8 } },"pablito");
        replay.Should().BeEquivalentTo(saved);
        (await Count("MATCH (:IdentityRelationshipObservation) RETURN count(*) AS count")).Should().Be(1);
        (await Count("MATCH (:IdentityRelationshipObservation)-[r:SOURCE_IDENTITY|TARGET_IDENTITY]->(:IdentityDecision) RETURN count(r) AS count")).Should().Be(2);
        (await Count("MATCH ()-[r:RELATED_TO]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Theory]
    [InlineData("foreign-owner")] [InlineData("surface")] [InlineData("provenance")]
    public async Task Invalid_relationship_endpoint_support_rolls_back_without_original_or_pointer_leaks(string change)
    {
        var input = await RelationshipObservationInput();
        if (change == "foreign-owner")
        {
            var foreign = await _decisions.SaveAsync(Input("Pepe","other"),"other");
            input = input with { SourceDecisionId = foreign.DecisionId };
        }
        if (change == "surface") input = input with { Observation = input.Observation with { SourceEntity = "Another person" } };
        if (change == "provenance") input = input with { SourceMessageIds = ["unrelated-message"] };
        var value = input;
        var save = () => _decisions.SaveRelationshipObservationAsync(value,"pablito");
        await save.Should().ThrowAsync<InvalidOperationException>();
        (await Count("MATCH (:IdentityRelationshipObservation) RETURN count(*) AS count")).Should().Be(0);
        (await Count("MATCH (:IdentityDecision)-[r:RESOLVES_TO]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task Original_relationship_support_keeps_the_per_decision_bound_before_creating_a_record()
    {
        var input = await RelationshipObservationInput();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync("MATCH (d:IdentityDecision {id:$id}) UNWIND range(1,256) AS i " +
                "CREATE (o:IdentityRelationshipObservation {id:'bounded-record-'+toString(i),owner_scope:'owned:pablito',owner_id:'pablito'}) " +
                "CREATE (o)-[:SOURCE_IDENTITY]->(d)",new { id = input.SourceDecisionId });
        });
        var save = () => _decisions.SaveRelationshipObservationAsync(input,"pablito");
        await save.Should().ThrowAsync<InvalidOperationException>();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito")).Should().BeNull();
        (await Count("MATCH (:IdentityRelationshipObservation) RETURN count(*) AS count")).Should().Be(256);
    }

    [Fact]
    public async Task Outer_ingestion_rollback_removes_the_original_relationship_and_its_new_endpoint_support()
    {
        var input = await RelationshipObservationInput();
        var save = () => _decisions.ExecuteScopedAsync<bool>("pablito",false,input.SourceMessageIds,async ct =>
        {
            await _decisions.SaveRelationshipObservationAsync(input,"pablito",cancellationToken:ct);
            throw new IOException("injected outer relationship rollback");
        });
        await save.Should().ThrowAsync<IOException>();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito")).Should().BeNull();
        (await Count("MATCH (:IdentityDecision)-[r:RESOLVES_TO]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task An_unresolved_relationship_is_inspectable_without_an_invented_endpoint()
    {
        var input = await RelationshipObservationInput();
        input = input with { SourceDecisionId = null, TargetDecisionId = null };
        var saved = await _decisions.SaveRelationshipObservationAsync(input,"pablito");
        saved.Observation.SourceEntity.Should().Be("Pepe"); saved.Observation.TargetEntity.Should().Be("Acme");
        saved.SourceDecisionId.Should().BeNull(); saved.TargetDecisionId.Should().BeNull();
        (await Count("MATCH (:IdentityRelationshipObservation)-[r:SOURCE_IDENTITY|TARGET_IDENTITY]->() RETURN count(r) AS count")).Should().Be(0);
    }

    private Neo4jRelationshipRepository RelationshipProjectionReader() => new(_fixture.TransactionRunner,
        NullLogger<Neo4jRelationshipRepository>.Instance, identity: Options.Create(new AgentMemory.Abstractions.Options.IdentityResolutionOptions { Enabled = true }), neo4j: _options);

    [Fact]
    public async Task Relationship_projection_reads_every_direction_and_ends_only_the_original_record()
    {
        var input = await RelationshipObservationInput();
        await _decisions.SaveRelationshipObservationAsync(input,"pablito");
        var source = (await _decisions.GetAsync(input.SourceDecisionId!,"pablito"))!.Observation.EntityId;
        var target = (await _decisions.GetAsync(input.TargetDecisionId!,"pablito"))!.Observation.EntityId;
        var reader = RelationshipProjectionReader(); var scope = MemoryScope.For("pablito",false);
        var relation = await reader.GetByIdAsync(input.ObservationId);
        relation!.SourceEntityId.Should().Be(source); relation.TargetEntityId.Should().Be(target);
        (await reader.GetBySourceEntityAsync(source,scope)).Should().ContainSingle();
        (await reader.GetByTargetEntityAsync(target,scope)).Should().ContainSingle();
        (await reader.GetByEntityAsync(source,scope)).Should().ContainSingle();
        (await reader.GetLiveAroundAsync([], ["Pepe"],16,DateTimeOffset.UtcNow,scope)).Should().ContainSingle();
        (await reader.GetLiveAroundAsync([], ["Pepe"],16,DateTimeOffset.UtcNow,MemoryScope.For("other",false))).Should().BeEmpty();
        (await reader.EndAsync(input.ObservationId,DateTimeOffset.UnixEpoch,MemoryScope.For("other",false))).Should().BeFalse();
        (await reader.EndAsync(input.ObservationId,DateTimeOffset.UnixEpoch,scope)).Should().BeTrue();
        (await reader.GetLiveAroundAsync([], ["Pepe"],16,DateTimeOffset.UtcNow,scope)).Should().BeEmpty();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito"))!.EndedAtUtc.Should().Be(DateTimeOffset.UnixEpoch);
        (await Count("MATCH ()-[r:RELATED_TO]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Fact]
    public async Task Original_relationship_ID_cannot_adopt_an_existing_ordinary_edge_ID()
    {
        var input = await RelationshipObservationInput();
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("CREATE (s:Entity {id:'collision-source'}), (t:Entity {id:'collision-target'}), " +
            "(s)-[:RELATED_TO {id:$id,owner_id:'other'}]->(t)",new { id=input.ObservationId }); });
        var save = () => _decisions.SaveRelationshipObservationAsync(input,"pablito");
        await save.Should().ThrowAsync<InvalidOperationException>();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito")).Should().BeNull();
        (await Count("MATCH ()-[r:RELATED_TO]->() RETURN count(r) AS count")).Should().Be(1);
    }

    [Fact]
    public async Task A_later_ordinary_ID_collision_fails_read_explicitly_and_ending_keeps_the_legacy_edge()
    {
        var input = await RelationshipObservationInput();
        await _decisions.SaveRelationshipObservationAsync(input,"pablito");
        var source = (await _decisions.GetAsync(input.SourceDecisionId!,"pablito"))!.Observation.EntityId;
        var target = (await _decisions.GetAsync(input.TargetDecisionId!,"pablito"))!.Observation.EntityId;
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        { await runner.RunAsync("MATCH (s:Entity {id:$source}), (t:Entity {id:$target}) CREATE (s)-[:RELATED_TO " +
            "{id:$id,owner_id:'pablito',source_entity_id:$source,target_entity_id:$target,relation_type:'WORKS_FOR',confidence:1.0,created_at:datetime('1970-01-01T00:00:00Z')}]->(t)",
            new { id=input.ObservationId,source,target }); });
        var reader=RelationshipProjectionReader();
        var get=()=>reader.GetByIdAsync(input.ObservationId);
        await get.Should().ThrowAsync<InvalidOperationException>();
        await reader.EndAsync(input.ObservationId,DateTimeOffset.UnixEpoch,MemoryScope.For("pablito",false));
        (await Count("MATCH ()-[r:RELATED_TO]->() WHERE r.valid_until IS NULL RETURN count(r) AS count")).Should().Be(1);
    }

    private Task RetireIdentityTarget(string id,string retirement) => _fixture.TransactionRunner.WriteAsync(async runner =>
    {
        if(retirement=="invalidated") await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.invalidated_at=datetime()",new { id });
        else await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.merged_into='replacement-identity'",new { id });
    });

    [Theory]
    [InlineData("invalidated")] [InlineData("merged")]
    public async Task Retired_target_cannot_receive_a_contextual_confirmation(string retirement)
    {
        var input=await ContextualInput();
        await RetireIdentityTarget(input.Decision.ConfirmedEntityId!,retirement);
        (await _decisions.SaveAsync(input,"pablito")).Status.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Theory]
    [InlineData("invalidated")] [InlineData("merged")]
    public async Task Retired_confirmed_endpoints_cannot_acquire_new_relationship_support(string retirement)
    {
        var plan=await Confirmed(); var source=await _decisions.SaveAsync(plan,"pablito");
        var target=await _decisions.SaveAsync(Input("Acme"),"pablito");
        var input=new IdentityRelationshipObservation { ObservationId="retired-endpoint-relation",OwnerId="pablito",SourceMessageIds=["message"],
            Observation=new() { SourceEntity="Pepe",TargetEntity="Acme",RelationshipType="WORKS_FOR" },
            SourceDecisionId=source.DecisionId,TargetDecisionId=target.DecisionId,CreatedAtUtc=DateTimeOffset.UnixEpoch };
        await RetireIdentityTarget(source.ConfirmedEntityId!,retirement);
        var save=()=>_decisions.SaveRelationshipObservationAsync(input,"pablito");
        await save.Should().ThrowAsync<InvalidOperationException>();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito")).Should().BeNull();
    }

    [Fact]
    public async Task Live_relationship_projection_hides_retired_identity_anchors_without_erasing_the_claim()
    {
        var input=await RelationshipObservationInput(); await _decisions.SaveRelationshipObservationAsync(input,"pablito");
        var source=(await _decisions.GetAsync(input.SourceDecisionId!,"pablito"))!.Observation.EntityId;
        await RetireIdentityTarget(source,"invalidated");
        (await RelationshipProjectionReader().GetLiveAroundAsync([], ["Pepe"],16,DateTimeOffset.UtcNow,MemoryScope.For("pablito",false))).Should().BeEmpty();
        (await _decisions.GetRelationshipObservationAsync(input.ObservationId,"pablito"))!.Observation.SourceEntity.Should().Be("Pepe");
    }

    private async Task<(IdentityDecision Decision,string Target,string Fact)> PendingReviewInput()
    {
        var target=await Seed("Pepe");
        var offered=await _evidence.FindExactCandidatesAsync(new() { Name="Pepe",Type="PERSON",OwnerId="pablito",Limit=12 });
        var fact=await Fact(); var input=Input();
        input=input with { ExpectedCandidates=offered.Candidates,Bindings=[new() { FactId=fact.FactId,Role=IdentityFactRole.Subject }],
            Decision=input.Decision with { Status=IdentityDecisionKind.Pending,PendingEntityIds=[target.EntityId] } };
        var saved=await _decisions.SaveAsync(input,"pablito");
        return(saved,target.EntityId,fact.FactId);
    }
    private static IdentityReviewRequest ReviewRequest(IdentityDecision decision,string operation,IdentityReviewAction action,string? target=null)=>new()
    { DecisionId=decision.DecisionId,OperationId=operation,ExpectedVersion=decision.Version,Action=action,CandidateEntityId=target,
        Actor="human:pablito",Reason="Fixture explicit identity review" };
    private Task<IdentityReviewResult> ApplyReview(IdentityReviewRequest request,string? owner="pablito",bool shared=false)=>
        ((AgentMemory.Abstractions.Repositories.IIdentityDecisionRepository)_decisions).ReviewAsync(request,owner,shared);
    private Task<IReadOnlyList<IdentityDecision>> ReviewHistory(string id,string? owner="pablito",int limit=32,long? before=null)=>
        ((AgentMemory.Abstractions.Repositories.IIdentityDecisionRepository)_decisions).GetHistoryAsync(id,owner,limit,before);

    [Fact]
    public async Task Explicit_review_confirm_and_revoke_move_only_owned_support_and_preserve_original_history()
    {
        var input=await PendingReviewInput();
        await _fixture.TransactionRunner.WriteAsync(async runner=>
        { await runner.RunAsync("MATCH (f:Fact {id:$fact}),(t:Entity {id:$target}) MERGE (f)-[:ABOUT]->(t)",new { fact=input.Fact,target=input.Target }); });
        var confirmed=await ApplyReview(ReviewRequest(input.Decision,"confirm",IdentityReviewAction.Confirm,input.Target));
        confirmed.CurrentDecision.Version.Should().Be(2); confirmed.CurrentDecision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        confirmed.CurrentDecision.ConfirmedEntityId.Should().Be(input.Target); confirmed.AlreadyApplied.Should().BeFalse();
        confirmed.CurrentDecision.Observation.Should().BeEquivalentTo(input.Decision.Observation);
        (await Count("MATCH (:Fact {id:$fact})-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(*) AS count",
            new { fact=input.Fact,target=input.Target })).Should().Be(1);
        var revoked=await ApplyReview(ReviewRequest(confirmed.CurrentDecision,"revoke",IdentityReviewAction.Revoke));
        revoked.CurrentDecision.Version.Should().Be(3); revoked.CurrentDecision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        revoked.CurrentDecision.ConfirmedEntityId.Should().BeNull(); revoked.CurrentDecision.Observation.Should().BeEquivalentTo(input.Decision.Observation);
        (await Count("MATCH (:Fact {id:$fact})-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(*) AS count",
            new { fact=input.Fact,target=input.Decision.Observation.EntityId })).Should().Be(1);
        (await Count("MATCH (:Fact {id:$fact})-[:ABOUT]->(:Entity {id:$target}) RETURN count(*) AS count",
            new { fact=input.Fact,target=input.Target })).Should().Be(1,"unowned legacy ABOUT support must survive");
        var history=await ReviewHistory(input.Decision.DecisionId);
        history.Select(value=>value.Version).Should().Equal(3,2,1);
        history.Last().Status.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public async Task Retrying_a_review_after_later_operations_returns_its_revision_without_reapplying_it()
    {
        var input=await PendingReviewInput(); var request=ReviewRequest(input.Decision,"confirm",IdentityReviewAction.Confirm,input.Target);
        var confirmed=await ApplyReview(request);
        var revoked=await ApplyReview(ReviewRequest(confirmed.CurrentDecision,"revoke",IdentityReviewAction.Revoke));
        var retry=await ApplyReview(request);
        retry.AlreadyApplied.Should().BeTrue(); retry.AppliedRevision.Version.Should().Be(2);
        retry.CurrentDecision.Version.Should().Be(3); retry.CurrentDecision.Status.Should().Be(revoked.CurrentDecision.Status);
        (await ReviewHistory(input.Decision.DecisionId)).Should().HaveCount(3);
        var changed=()=>ApplyReview(request with { Reason="Changed retry payload" });
        await changed.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Rejecting_a_pending_candidate_cannot_be_silently_reproposed_by_ingestion_replay()
    {
        var input=await PendingReviewInput();
        var rejected=await ApplyReview(ReviewRequest(input.Decision,"reject",IdentityReviewAction.Reject,input.Target));
        rejected.CurrentDecision.Version.Should().Be(2); rejected.CurrentDecision.PendingEntityIds.Should().NotContain(input.Target);
        rejected.CurrentDecision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        (await Count("MATCH (:IdentityDecision {id:$id})-[:POSSIBLE_SAME_AS]->() RETURN count(*) AS count",new { id=input.Decision.DecisionId })).Should().Be(0);
        var replay=await _decisions.SaveAsync(new() { Decision=input.Decision,
            ExpectedCandidates=(await _evidence.FindExactCandidatesAsync(new() { Name="Pepe",Type="PERSON",OwnerId="pablito",Limit=12 })).Candidates },"pablito");
        replay.Version.Should().Be(2); replay.PendingEntityIds.Should().NotContain(input.Target);
    }

    [Fact]
    public async Task The_review_queue_lists_only_the_owners_open_proposals_and_drops_reviewed_ones()
    {
        // ILC-19: without a listing a host cannot build a review queue, so Pending proposals only accumulate.
        var input=await PendingReviewInput();
        var queue=await _decisions.ListPendingAsync("pablito",10);
        queue.Should().ContainSingle().Which.DecisionId.Should().Be(input.Decision.DecisionId);
        queue[0].PendingEntityIds.Should().Contain(input.Target);
        (await _decisions.ListPendingAsync("other",10)).Should().BeEmpty();
        (await _decisions.ListPendingAsync(null,10)).Should().BeEmpty();
        await ApplyReview(ReviewRequest(input.Decision,"reject-queue",IdentityReviewAction.Reject,input.Target));
        (await _decisions.ListPendingAsync("pablito",10)).Should().BeEmpty("a reviewed proposal leaves the queue");
    }

    [Fact]
    public async Task The_review_queue_is_bounded_and_pages_in_a_stable_order()
    {
        await PendingReviewInput();
        var first=await _decisions.ListPendingAsync("pablito",1);
        first.Should().ContainSingle();
        (await _decisions.ListPendingAsync("pablito",1,first[0].DecisionId)).Should().BeEmpty();
        await FluentActions.Awaiting(()=>_decisions.ListPendingAsync("pablito",0)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions.Awaiting(()=>_decisions.ListPendingAsync("pablito",257)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Stale_or_foreign_reviews_cannot_overwrite_a_committed_revision()
    {
        var input=await PendingReviewInput(); var request=ReviewRequest(input.Decision,"confirm",IdentityReviewAction.Confirm,input.Target);
        var foreign=()=>ApplyReview(request,"other"); await foreign.Should().ThrowAsync<InvalidOperationException>();
        (await ReviewHistory(input.Decision.DecisionId,"other")).Should().BeEmpty();
        await ApplyReview(request);
        var stale=()=>ApplyReview(request with { OperationId="stale-other-operation" });
        await stale.Should().ThrowAsync<InvalidOperationException>();
        (await ReviewHistory(input.Decision.DecisionId)).Should().HaveCount(2);
        (await ReviewHistory(input.Decision.DecisionId,limit:1)).Should().ContainSingle();
        (await ReviewHistory(input.Decision.DecisionId,before:2)).Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task A_review_rolls_back_at_every_statement_and_before_commit_without_changing_history_or_support()
    {
        var input=await PendingReviewInput(); var request=ReviewRequest(input.Decision,"faulted-review",IdentityReviewAction.Confirm,input.Target);
        var beforeCommit=new FaultRunner(_fixture.TransactionRunner,0);
        var repository=new Neo4jIdentityDecisionRepository(beforeCommit,_options);
        var act=()=>repository.ReviewAsync(request,"pablito");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected*");
        beforeCommit.StatementCount.Should().BeGreaterThan(10,"the failure must follow all actual writes");
        for(var statement=1;statement<=beforeCommit.StatementCount;statement++)
        {
            var failed=new Neo4jIdentityDecisionRepository(new FaultRunner(_fixture.TransactionRunner,statement),_options);
            var attempt=()=>failed.ReviewAsync(request,"pablito");
            await attempt.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected*");
            (await _decisions.GetAsync(input.Decision.DecisionId,"pablito")).Should().BeEquivalentTo(input.Decision);
            (await ReviewHistory(input.Decision.DecisionId)).Should().ContainSingle();
            (await Count("MATCH (:Fact {id:$fact})-[:HAS_IDENTITY_BINDING]->(:IdentityFactBinding)-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(*) AS count",
                new { fact=input.Fact,target=input.Decision.Observation.EntityId })).Should().Be(1);
            (await Count("MATCH (:IdentityDecision {id:$id})-[:POSSIBLE_SAME_AS]->() RETURN count(*) AS count",
                new { id=input.Decision.DecisionId })).Should().Be(1);
        }
        (await ApplyReview(request)).CurrentDecision.Version.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_reviews_of_one_version_commit_exactly_one_revision_and_retry_the_winner()
    {
        var input=await PendingReviewInput();
        var requests=Enumerable.Range(0,4).Select(i=>ReviewRequest(input.Decision,"concurrent-review-"+i,IdentityReviewAction.Confirm,input.Target)).ToArray();
        var attempts=requests.Select(async request=>
        {
            try { return (Request:request,Result:await ApplyReview(request),Error:(Exception?)null); }
            catch(Exception error) { return (Request:request,Result:(IdentityReviewResult?)null,Error:error); }
        });
        var results=await Task.WhenAll(attempts);
        var winner=results.Where(result=>result.Result is not null).Should().ContainSingle().Subject;
        results.Where(result=>result.Error is not null).Should().HaveCount(3)
            .And.OnlyContain(result=>result.Error is InvalidOperationException && result.Error.Message.Contains("stale"));
        (await ReviewHistory(input.Decision.DecisionId)).Select(value=>value.Version).Should().Equal(2,1);
        (await ApplyReview(winner.Request)).AlreadyApplied.Should().BeTrue();
    }

    [Fact]
    public async Task A_review_with_a_lost_commit_acknowledgement_is_discoverable_after_repository_restart()
    {
        var input=await PendingReviewInput(); var request=ReviewRequest(input.Decision,"lost-review-ack",IdentityReviewAction.Confirm,input.Target);
        var ambiguous=new Neo4jIdentityDecisionRepository(new LostAcknowledgementRunner(_fixture.TransactionRunner),_options);
        var act=()=>ambiguous.ReviewAsync(request,"pablito"); await act.Should().ThrowAsync<IOException>();
        var restarted=new Neo4jIdentityDecisionRepository(_fixture.TransactionRunner,_options);
        var retry=await restarted.ReviewAsync(request,"pablito");
        retry.AlreadyApplied.Should().BeTrue(); retry.CurrentDecision.Version.Should().Be(2);
        (await restarted.GetHistoryAsync(input.Decision.DecisionId,"pablito")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Facts_added_after_manual_confirmation_follow_the_decision_and_return_to_original_on_revoke()
    {
        var input=await PendingReviewInput();
        var confirmed=await ApplyReview(ReviewRequest(input.Decision,"confirm-for-later-fact",IdentityReviewAction.Confirm,input.Target));
        var later=await Fact("Pepe","planning the rehearsal");
        var plan=new IdentityDecisionWrite { Decision=input.Decision,
            ExpectedCandidates=(await _evidence.FindExactCandidatesAsync(new() { Name="Pepe",Type="PERSON",OwnerId="pablito",Limit=12 })).Candidates,
            Bindings=[new() { FactId=later.FactId,Role=IdentityFactRole.Subject }] };
        var restarted=new Neo4jIdentityDecisionRepository(_fixture.TransactionRunner,_options);
        (await restarted.SaveAsync(plan,"pablito")).Version.Should().Be(2);
        (await Count("MATCH (b:IdentityFactBinding {identity_decision_id:$id})-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(b) AS count",
            new { id=input.Decision.DecisionId,target=input.Target })).Should().Be(2);
        await ApplyReview(ReviewRequest(confirmed.CurrentDecision,"revoke-later-fact",IdentityReviewAction.Revoke));
        (await Count("MATCH (b:IdentityFactBinding {identity_decision_id:$id})-[:RESOLVES_TO]->(:Entity {id:$target}) RETURN count(b) AS count",
            new { id=input.Decision.DecisionId,target=input.Decision.Observation.EntityId })).Should().Be(2);
        (await _facts.GetByIdAsync(later.FactId))!.Subject.Should().Be("Pepe");
    }

    [Fact]
    public async Task Original_relationship_projection_follows_only_the_reviewed_endpoint_including_later_claims()
    {
        var input=await PendingReviewInput(); var other=await _decisions.SaveAsync(Input("Acme"),"pablito");
        var original=new IdentityRelationshipObservation { ObservationId="review-original-claim",OwnerId="pablito",SourceMessageIds=["message"],
            Observation=new() { SourceEntity="Pepe",TargetEntity="Acme",RelationshipType="WORKS_FOR" },
            SourceDecisionId=input.Decision.DecisionId,TargetDecisionId=other.DecisionId,CreatedAtUtc=DateTimeOffset.UnixEpoch };
        await _decisions.SaveRelationshipObservationAsync(original,"pablito");
        var confirmed=await ApplyReview(ReviewRequest(input.Decision,"confirm-for-relationship",IdentityReviewAction.Confirm,input.Target));
        var reader=RelationshipProjectionReader();
        (await reader.GetByIdAsync(original.ObservationId))!.SourceEntityId.Should().Be(input.Target);
        var later=original with { ObservationId="review-later-claim",Observation=original.Observation with { RelationshipType="MANAGES" } };
        await _decisions.SaveRelationshipObservationAsync(later,"pablito");
        await ApplyReview(ReviewRequest(confirmed.CurrentDecision,"revoke-relationship",IdentityReviewAction.Revoke));
        foreach(var id in new[] { original.ObservationId,later.ObservationId })
        {
            var projected=await reader.GetByIdAsync(id);
            projected!.SourceEntityId.Should().Be(input.Decision.Observation.EntityId);
            projected.TargetEntityId.Should().Be(other.Observation.EntityId);
        }
        (await _decisions.GetRelationshipObservationAsync(original.ObservationId,"pablito")).Should().BeEquivalentTo(original);
        (await Count("MATCH ()-[r:RELATED_TO]->() RETURN count(r) AS count")).Should().Be(0);
    }

    [Theory]
    [InlineData("invalidated")] [InlineData("merged")] [InlineData("foreign")]
    public async Task Review_confirmation_refuses_targets_that_are_no_longer_live_in_scope(string change)
    {
        var input=await PendingReviewInput();
        if(change=="foreign") await _fixture.TransactionRunner.WriteAsync(async runner=>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.owner_id='other'",new { id=input.Target }); });
        else await RetireIdentityTarget(input.Target,change);
        var act=()=>ApplyReview(ReviewRequest(input.Decision,"bad-target-review",IdentityReviewAction.Confirm,input.Target));
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReviewHistory(input.Decision.DecisionId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)] [InlineData("*")] [InlineData("**")]
    public async Task Explicit_review_uses_exact_owner_scope_even_for_reserved_looking_owner_values(string? owner)
    {
        var target=await Seed("Pepe",owner);
        var offered=await _evidence.FindExactCandidatesAsync(new() { Name="Pepe",Type="PERSON",OwnerId=owner,Limit=12 });
        var plan=Input(owner:owner); plan=plan with { ExpectedCandidates=offered.Candidates,
            Decision=plan.Decision with { Status=IdentityDecisionKind.Pending,PendingEntityIds=[target.EntityId] } };
        var pending=await _decisions.SaveAsync(plan,owner);
        var confirmed=await ApplyReview(ReviewRequest(pending,"exact-owner-review",IdentityReviewAction.Confirm,target.EntityId),owner);
        confirmed.CurrentDecision.ConfirmedEntityId.Should().Be(target.EntityId);
        (await ReviewHistory(pending.DecisionId,"foreign")).Should().BeEmpty();
        var foreign=()=>ApplyReview(ReviewRequest(confirmed.CurrentDecision,"foreign-revoke",IdentityReviewAction.Revoke),"foreign");
        await foreign.Should().ThrowAsync<InvalidOperationException>();
        (await ReviewHistory(pending.DecisionId,owner)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("December 12, 2026","December 3, 2026",false)]
    [InlineData("December 12, 2026","December 4, 2026",false)]
    [InlineData("December 12, 2026","12 diciembre 2026",true)]
    [InlineData("December 12","December 12, 2026",false)]
    public async Task Explicit_review_cannot_treat_calendar_similarity_as_date_identity(string observed,string existing,bool allowed)
    {
        var target=await _entities.UpsertAsync(new() { EntityId=Id(),Name=existing,Type="DATE",OwnerId="pablito",Confidence=1,CreatedAtUtc=DateTimeOffset.UnixEpoch });
        var plan=Input(observed); plan=plan with { Decision=plan.Decision with { Observation=plan.Decision.Observation with { Type="DATE" } } };
        var pending=await _decisions.SaveAsync(plan,"pablito");
        var request=ReviewRequest(pending,"calendar-review",IdentityReviewAction.Confirm,target.EntityId);
        if(allowed) (await ApplyReview(request)).CurrentDecision.ConfirmedEntityId.Should().Be(target.EntityId);
        else
        {
            var act=()=>ApplyReview(request); await act.Should().ThrowAsync<InvalidOperationException>();
            (await ReviewHistory(pending.DecisionId)).Should().ContainSingle();
        }
        (await _entities.GetByIdAsync(target.EntityId))!.Name.Should().Be(existing);
    }

    [Theory]
    [InlineData("foreign")] [InlineData("invalidated")]
    public async Task Reject_review_does_not_reassert_an_alternative_that_has_left_the_visible_live_scope(string change)
    {
        var first=await Seed("Pepe"); var alternative=await Seed("Pepe");
        var offered=await _evidence.FindExactCandidatesAsync(new() { Name="Pepe",Type="PERSON",OwnerId="pablito",Limit=12 });
        var plan=Input(); plan=plan with { ExpectedCandidates=offered.Candidates,
            Decision=plan.Decision with { Status=IdentityDecisionKind.Pending,PendingEntityIds=[first.EntityId,alternative.EntityId] } };
        var pending=await _decisions.SaveAsync(plan,"pablito");
        if(change=="foreign") await _fixture.TransactionRunner.WriteAsync(async runner=>
        { await runner.RunAsync("MATCH (e:Entity {id:$id}) SET e.owner_id='other'",new { id=alternative.EntityId }); });
        else await RetireIdentityTarget(alternative.EntityId,change);
        var result=await ApplyReview(ReviewRequest(pending,"reject-with-stale-alternative",IdentityReviewAction.Reject,first.EntityId));
        result.CurrentDecision.PendingEntityIds.Should().BeEmpty("a review must not recreate proposals to invisible or retired alternatives");
        result.CurrentDecision.Status.Should().Be(IdentityDecisionKind.New);
        (await Count("MATCH (:IdentityDecision {id:$id})-[:POSSIBLE_SAME_AS]->() RETURN count(*) AS count",new { id=pending.DecisionId })).Should().Be(0);
        (await _decisions.GetAsync(pending.DecisionId,"pablito"))!.Observation.Should().BeEquivalentTo(pending.Observation);
    }

}
