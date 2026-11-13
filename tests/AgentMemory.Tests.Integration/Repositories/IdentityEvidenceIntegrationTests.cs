using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class IdentityEvidenceIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jIdentityEvidenceRepository _identity;
    private readonly Neo4jEntityRepository _entities;
    public IdentityEvidenceIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var options = new Neo4jOptions(); options.Extensions.Add("identity-evidence");
        _identity = new(fixture.TransactionRunner, Options.Create(options));
        _entities = new(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }
    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Schema/Migrations/ext/identity-evidence/0001_identity_evidence.cypher"));
        foreach (var statement in migration.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await _fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(statement); });
    }
    public Task DisposeAsync() => Task.CompletedTask;
    private Task<Entity> Seed(string name, string? owner = "pablito", string type = "PERSON", string? canonical = null) => _entities.UpsertAsync(new Entity
    { EntityId = Guid.NewGuid().ToString("N"), Name = name, CanonicalName = canonical, Type = type, OwnerId = owner, Confidence = 1, CreatedAtUtc = DateTimeOffset.UtcNow });
    private static VerifiedIdentityKey Key(string id = "42", string source = "directory:42") => new()
    { Namespace = "staff", Authority = "directory", SourceId = source, Values = new Dictionary<string, string> { ["employee_id"] = id, ["ignored"] = "context" } };
    private static IdentityCandidateQuery Query(string name = "Jose", string? owner = "pablito", int limit = 12) => new()
    { Name = name, Type = "PERSON", OwnerId = owner, Limit = limit };

    [Fact]
    public async Task Verified_key_lookup_uses_only_the_configured_components_and_round_trips_provenance()
    {
        var entity = await Seed("José García");
        await _identity.AssertKeyAsync(entity.EntityId, Key(), ["employee_id"], "pablito");
        var result = await _identity.FindExactCandidatesAsync(Query("unrelated spelling") with
        { VerifiedKeys = [Key() with { Values = new Dictionary<string, string> { ["employee_id"] = "42" } }], IdentityProperties = ["employee_id"] });
        result.ExactIdentityLookupComplete.Should().BeTrue();
        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.Entity.EntityId.Should().Be(entity.EntityId);
        candidate.VerifiedKeys.Should().ContainSingle().Which.Values.Keys.Should().Equal("employee_id");
        candidate.VerifiedKeys[0].Authority.Should().Be("directory");
        candidate.VerifiedKeys[0].SourceId.Should().Be("directory:42");
        candidate.EvidenceVersion.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(null, false, 1)]
    [InlineData("pablito", false, 1)]
    [InlineData("pablito", true, 2)]
    [InlineData("*", false, 1)]
    public async Task Exact_lookup_enforces_owner_and_shared_visibility(string? owner, bool includeShared, int expected)
    {
        foreach (var o in new string?[] { null, "pablito", "other", "*" }) await Seed("Jose", o);
        var result = await _identity.FindExactCandidatesAsync(Query(owner: owner) with { IncludeShared = includeShared });
        result.Candidates.Should().HaveCount(expected);
        result.Candidates.Should().OnlyContain(c => c.Entity.OwnerId == owner || includeShared && c.Entity.OwnerId == null);
    }

    [Fact]
    public async Task Duplicate_exact_matches_report_truncation_before_context_hydration()
    {
        for (var i = 0; i < 4; i++) await Seed("Jose");
        var result = await _identity.FindExactCandidatesAsync(Query(limit: 2));
        result.Candidates.Should().HaveCount(2);
        result.ExactIdentityLookupComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Backed_aliases_and_name_namespaces_are_exact_and_preserve_the_primary_name()
    {
        var entity = await Seed("José García");
        await _identity.AssertAliasAsync(entity.EntityId, new() { Surface = "Pepe", Namespace = "staff", SourceId = "review:1" }, "human", "pablito");
        await _identity.AssertNameNamespaceAsync(entity.EntityId, "staff", "directory", "names:1", "pablito");
        var alias = await _identity.FindExactCandidatesAsync(Query("Pepe") with { NameNamespace = "staff" });
        alias.Candidates.Should().ContainSingle().Which.VerifiedAliases.Should().ContainSingle().Which.SourceId.Should().Be("review:1");
        (await _identity.FindExactCandidatesAsync(Query("pepe") with { NameNamespace = "staff" })).Candidates.Should().BeEmpty();
        (await _identity.FindExactCandidatesAsync(Query("Pepe") with { NameNamespace = "other" })).Candidates.Should().BeEmpty();
        var primary = await _identity.FindExactCandidatesAsync(Query("José García") with { NameNamespace = "staff" });
        primary.Candidates.Should().ContainSingle().Which.NameNamespace.Should().Be("staff");
        (await _entities.GetByIdAsync(entity.EntityId))!.Name.Should().Be("José García");
    }

    [Theory]
    [InlineData("December 12, 2026", "CalendarDate", "gregorian:2026-12-12")]
    [InlineData("9–13 diciembre 2026", "CalendarInterval", "gregorian-range:2026-12-09/2026-12-13")]
    public async Task Calendar_lookup_uses_host_kind_and_normalised_values(string surface, string kind, string key)
    {
        var entity = await Seed(surface, type: "OBJECT");
        await _identity.AssertKindAsync(entity.EntityId, new() { Kind = Enum.Parse<IdentitySubjectKind>(kind), Authority = "calendar", SourceId = "event:1" }, "pablito");
        var result = await _identity.FindExactCandidatesAsync(Query("a different rendering") with { Type = "OBJECT", CalendarKey = key });
        result.Candidates.Should().ContainSingle().Which.VerifiedKind!.Kind.Should().Be(Enum.Parse<IdentitySubjectKind>(kind));
        (await _identity.FindExactCandidatesAsync(Query("a different rendering") with { Type = "OBJECT", CalendarKey = "gregorian:2026-12-03" })).Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Conflicting_kind_is_rejected_atomically_and_does_not_poison_existing_evidence()
    {
        var entity = await Seed("2026-12-12", type: "OBJECT");
        await _identity.AssertKindAsync(entity.EntityId, new() { Kind = IdentitySubjectKind.CalendarDate, Authority = "calendar", SourceId = "event:1" }, "pablito");
        var conflict = () => _identity.AssertKindAsync(entity.EntityId, new() { Kind = IdentitySubjectKind.Reference, Authority = "other", SourceId = "product:1" }, "pablito");
        await conflict.Should().ThrowAsync<InvalidOperationException>();
        var result = await _identity.FindExactCandidatesAsync(Query("2026-12-12") with { Type = "OBJECT" });
        result.Candidates.Should().ContainSingle().Which.VerifiedKind!.Kind.Should().Be(IdentitySubjectKind.CalendarDate);
    }

    [Fact]
    public async Task Contradictory_calendar_primary_and_canonical_values_cannot_be_asserted()
    {
        var entity = await Seed("2026-12-12", type: "OBJECT", canonical: "2026-12-03");
        var action = () => _identity.AssertKindAsync(entity.EntityId, new() { Kind = IdentitySubjectKind.CalendarDate, Authority = "calendar", SourceId = "event:1" }, "pablito");
        await action.Should().ThrowAsync<InvalidOperationException>();
        (await _identity.FindExactCandidatesAsync(Query("other") with { Type = "OBJECT", CalendarKey = "gregorian:2026-12-12" })).Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Same_assertion_is_idempotent_but_conflicting_keys_remain_visible_for_a_veto()
    {
        var entity = await Seed("Jose");
        await _identity.AssertKeyAsync(entity.EntityId, Key(), ["employee_id"], "pablito");
        await _identity.AssertKeyAsync(entity.EntityId, Key(), ["employee_id"], "pablito");
        await _identity.AssertKeyAsync(entity.EntityId, Key("99", "directory:99"), ["employee_id"], "pablito");
        var result = await _identity.FindExactCandidatesAsync(Query());
        result.Candidates.Should().ContainSingle().Which.VerifiedKeys.Should().HaveCount(2);
        result.Candidates[0].VerifiedKeys.Select(k => k.Values["employee_id"]).Should().BeEquivalentTo("42", "99");
    }

    [Fact]
    public async Task Evidence_overflow_is_explicit_and_never_silently_drops_contradictions()
    {
        var entity = await Seed("Jose");
        for (var i = 0; i < 65; i++)
            await _identity.AssertAliasAsync(entity.EntityId, new() { Surface = "alias " + i, Namespace = "staff", SourceId = "review:" + i }, "human", "pablito");
        var result = await _identity.FindExactCandidatesAsync(Query());
        result.Candidates.Should().ContainSingle().Which.EvidenceComplete.Should().BeFalse();
        result.Candidates[0].VerifiedAliases.Should().HaveCount(64);
    }

    [Fact]
    public async Task Host_cannot_write_another_owner_or_shared_entity_through_an_owned_scope()
    {
        var other = await Seed("Jose", "other"); var shared = await Seed("Jose", null);
        foreach (var id in new[] { other.EntityId, shared.EntityId })
        {
            var action = () => _identity.AssertKeyAsync(id, Key(), ["employee_id"], "pablito");
            await action.Should().ThrowAsync<InvalidOperationException>();
        }
        var result = await _identity.FindExactCandidatesAsync(Query(owner: null));
        result.Candidates.Should().ContainSingle().Which.VerifiedKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_compatible_types_are_admitted_without_admitting_unrelated_types()
    {
        await Seed("Jose", type: "CONTACT"); await Seed("Jose", type: "LOCATION");
        (await _identity.FindExactCandidatesAsync(Query())).Candidates.Should().BeEmpty();
        (await _identity.FindExactCandidatesAsync(Query() with { CompatibleTypes = ["CONTACT"] })).Candidates.Should().ContainSingle().Which.Entity.Type.Should().Be("CONTACT");
    }

    [Fact]
    public async Task Approximate_fulltext_and_vector_candidates_are_bounded_and_scoped()
    {
        for (var i = 0; i < 5; i++)
        {
            var entity = await Seed("Jose Garcia " + i);
            await _entities.UpsertAsync(entity with { Embedding = [1, 0, 0, 0] });
        }
        await Seed("Jose Garcia other", "other");
        await _fixture.TransactionRunner.ReadAsync(async runner => { await runner.RunAsync("CALL db.index.fulltext.awaitEventuallyConsistentIndexRefresh()"); });
        var lexical = await _identity.FindApproximateCandidatesAsync(Query("Jose Garcia", limit: 2), null);
        lexical.Candidates.Should().HaveCount(2).And.OnlyContain(c => c.Entity.OwnerId == "pablito");
        lexical.Candidates.Should().OnlyContain(c => c.Signals.FuzzySimilarity >= 0 && c.Signals.FuzzySimilarity <= 1);
        var vector = await _identity.FindApproximateCandidatesAsync(Query("unrelated", limit: 2) with { UseFuzzyCandidates = false }, [1, 0, 0, 0]);
        vector.Candidates.Should().HaveCount(2).And.OnlyContain(c => c.Entity.OwnerId == "pablito");
        vector.Candidates.Should().OnlyContain(c => c.Signals.FuzzySimilarity == null && c.Signals.SemanticSimilarity >= -1 && c.Signals.SemanticSimilarity <= 1);
    }

    private Task SeedSourceMessages() => _fixture.TransactionRunner.WriteAsync(async runner =>
    {
        await runner.RunAsync(
            "CREATE (:Conversation {id:'c-own', user_id:'pablito'}), (:Conversation {id:'c-other', user_id:'other'}), (:Conversation {id:'c-shared'}), " +
            "(:Message {id:'own', conversation_id:'c-own', content:'I am leading the billing go-live on December 11.'}), " +
            "(:Message {id:'own-2', conversation_id:'c-own', content:'The go-live kickoff call is at 7 am.'}), " +
            "(:Message {id:'foreign', conversation_id:'c-other', content:'Another owner talks about a go-live.'}), " +
            "(:Message {id:'shared', conversation_id:'c-shared', content:'Shared team calendar.'}), " +
            "(:Message {id:'gone', conversation_id:'c-own', content:'Withdrawn text.', invalidated_at: datetime()})");
    });

    [Theory]
    [InlineData("pablito", false, new[] { "own" })]
    [InlineData("pablito", true, new[] { "own", "shared" })]
    [InlineData(null, false, new[] { "shared" })]
    [InlineData("other", false, new[] { "foreign" })]
    public async Task Candidate_sources_are_read_only_within_the_decision_owner_scope(string? owner, bool includeShared, string[] expected)
    {
        await SeedSourceMessages();
        var sources = await _identity.GetSourcesAsync(["own", "foreign", "shared", "gone", "missing"], owner, includeShared, 5);
        sources.Select(source => source.SourceId).Should().BeEquivalentTo(expected);
        sources.Should().OnlyContain(source => source.OriginIds.SequenceEqual(new[] { source.SourceId }) && source.Text.Length > 0);
        if (expected.Contains("own")) sources.Single(source => source.SourceId == "own").Text.Should().Be("I am leading the billing go-live on December 11.");
    }

    [Fact]
    public async Task Candidate_source_reads_apply_their_limit_in_the_query()
    {
        await SeedSourceMessages();
        (await _identity.GetSourcesAsync(["own", "own-2"], "pablito", false, 1)).Should().ContainSingle();
    }

    [Fact]
    public async Task Context_rooted_candidates_are_the_scoped_same_type_entities_observed_in_the_context_turns()
    {
        await _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync("""
                CREATE (ctx:Message {id:'ctx', conversation_id:'c', content:'I lead the go-live.'}),
                       (other:Message {id:'other', conversation_id:'c', content:'Unrelated.'}),
                       (a:Entity {id:'event-a', name:'Acme go-live', type:'EVENT', owner_id:'pablito'})-[:EXTRACTED_FROM]->(ctx),
                       (b:Entity {id:'event-b', name:'Wedding', type:'EVENT', owner_id:'pablito'})-[:EXTRACTED_FROM]->(other),
                       (p:Entity {id:'person', name:'Diego', type:'PERSON', owner_id:'pablito'})-[:EXTRACTED_FROM]->(ctx),
                       (f:Entity {id:'foreign', name:'Acme go-live', type:'EVENT', owner_id:'other'})-[:EXTRACTED_FROM]->(ctx),
                       (o:Entity {id:'identity-observation-k', name:'the launch', type:'EVENT', owner_id:'pablito', identity_observation_key:'k'})-[:EXTRACTED_FROM]->(ctx),
                       (t:Entity {id:'event-t', name:'Billing launch', type:'EVENT', owner_id:'pablito'}),
                       (d:IdentityDecision {id:'identity-decision-k', identity_observation_key:'k', owner_scope:'owned:pablito', owner_id:'pablito'})-[:RESOLVES_TO]->(t)
                """);
        });
        var result = await _identity.FindApproximateCandidatesAsync(new IdentityCandidateQuery
        { Name = "Cutover", Type = "EVENT", OwnerId = "pablito", Limit = 8, UseFuzzyCandidates = false, ContextSourceMessageIds = ["ctx"] }, null);
        result.Candidates.Select(c => c.Entity.EntityId).Should().BeEquivalentTo(["event-a", "event-t"]);
        result.Candidates.Should().OnlyContain(c => c.Signals.ContextRooted);
        _identity.Capabilities.ContextRootedLookup.Should().BeTrue();
    }

    [Fact]
    public async Task Candidate_source_reads_refuse_when_the_identity_extension_is_off()
    {
        var disabled = new Neo4jIdentityEvidenceRepository(_fixture.TransactionRunner, Options.Create(new Neo4jOptions()));
        await disabled.Invoking(repository => repository.GetSourcesAsync(["own"], "pablito", false, 1))
            .Should().ThrowAsync<InvalidOperationException>();
    }
}
