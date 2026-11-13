using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// ILCS native cells (ILC-04): every registered typed pair is written as two sequential turns through the real
/// extraction and persistence stages on Neo4j, then checked for its identity decision, decision-owned fact bindings,
/// preserved original surfaces and a fresh-host read. Test names carry their ILCS cell (id, configuration, variant,
/// check) for the lifecycle reporter. Host keys are supplied only for cases whose fixtures declare them.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class IdentityLifecycleNativeTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    private static string CorpusPath([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../../AgentMemory.Tests.Unit/TestData/Identity/lifecycle-cases.json"));

    private static IEnumerable<object[]> Cases(string check)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CorpusPath()));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "identity-pair")
            .Select(item => new object[] { item.GetProperty("id").GetString()!, Configuration(item), "sequential", check, item.GetRawText() }).ToArray();
    }

    public static IEnumerable<object[]> BindingCases() => Cases("binding-roundtrip");
    public static IEnumerable<object[]> ReadCases() => Cases("fresh-scope-read");

    /// <summary>The ILCS configuration a pair runs in: host evidence only where its fixtures declare keys or a kind.</summary>
    internal static string Configuration(JsonElement item) =>
        new[] { "seed", "mention" }.Any(role => item.GetProperty(role).GetProperty("keys").EnumerateObject().Any() ||
            item.GetProperty(role).GetProperty("kind").ValueKind == JsonValueKind.String) ? "host-evidence" : "evidence-deterministic";

    public async Task InitializeAsync()
    {
        await fixture.CleanDatabaseAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Schema/Migrations/ext/identity-evidence"), "*.cypher").Order())
            foreach (var statement in (await File.ReadAllTextAsync(file)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await fixture.TransactionRunner.WriteAsync(async runner => { await runner.RunAsync(statement); });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Text(JsonElement e, string key) => e.GetProperty(key).GetString()!;

    private ServiceProvider Host(JsonElement item)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<INeo4jTransactionRunner>(fixture.TransactionRunner);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0, 0 });
        embeddings.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0, 0, 0 });
        services.AddSingleton(embeddings);
        services.AddAgentMemoryCore(_ => { });
        services.Configure<MemoryOptions>(options => options.WorkingMemory.Enabled = false);
        services.Configure<ExtractionOptions>(options => { options.CanonicalFactSubjects = true; options.LinkFactsToEntities = true; options.DeduplicateWithinExtraction = true; });
        var definition = item.GetProperty("policy");
        services.Configure<IdentityResolutionOptions>(options =>
        {
            options.Enabled = true; options.MaxAdjudicationsPerIngestion = 0;
            // The corpus policy belongs to the pair: both its types use it, so a host key on either is a complete key.
            foreach (var type in new[] { Text(item.GetProperty("seed"), "type"), Text(item.GetProperty("mention"), "type") }.Distinct(StringComparer.OrdinalIgnoreCase))
                options.Policies[type] = new()
                {
                    IdentityProperties = [.. definition.GetProperty("identityProperties").EnumerateArray().Select(p => p.GetString()!)],
                    CompatibleTypes = [.. definition.GetProperty("compatibleTypes").EnumerateArray().Select(p => p.GetString()!)],
                };
        });
        if (Configuration(item) == "host-evidence") services.AddSingleton(HostEvidence(item));
        services.AddNeo4jAgentMemory(options =>
        { options.Uri = "bolt://unused.test:7687"; options.Username = "unused"; options.Password = "unused"; options.Extensions.Add("identity-evidence"); });
        return services.BuildServiceProvider();
    }

    /// <summary>The fixture's own assertions for exactly the mention and source it declares them on; nothing invented.</summary>
    private static IIdentityContextProvider HostEvidence(JsonElement item)
    {
        var declared = new[] { item.GetProperty("seed").Clone(), item.GetProperty("mention").Clone() };
        var host = Substitute.For<IIdentityContextProvider>();
        host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mention = call.ArgAt<ExtractedEntity>(0); var sources = call.ArgAt<EntityIdentityResolutionContext>(1);
                var match = declared.FirstOrDefault(e => Text(e, "name") == mention.Name && sources.SourceMessageIds.Contains(Text(e, "sourceId")));
                if (match.ValueKind != JsonValueKind.Object) return Task.FromResult<IdentityHostEvidence?>(null);
                var keys = match.GetProperty("keys").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
                return Task.FromResult<IdentityHostEvidence?>(new IdentityHostEvidence
                {
                    Keys = keys.Count == 0 ? [] : [new VerifiedIdentityKey { Namespace = "lifecycle-fixture", Authority = "synthetic-host", SourceId = Text(match, "sourceId"), Values = keys }],
                    Kind = match.GetProperty("kind").ValueKind == JsonValueKind.String
                        ? new VerifiedIdentityKind { Kind = Enum.Parse<IdentitySubjectKind>(Text(match, "kind")), Authority = "synthetic-host", SourceId = Text(match, "sourceId") }
                        : null,
                });
            });
        return host;
    }

    private sealed record Outcome(IReadOnlyList<IdentityDecision> Decisions, IReadOnlyList<(string FactId, string Role, string Target, string Decision)> Bindings,
        IReadOnlyDictionary<string, Fact> FactsByKey);

    /// <summary>Writes both turns in order, each in its own host scope, as two separate conversation turns.</summary>
    private async Task<Outcome> Run(JsonElement item)
    {
        var owner = Text(item, "owner");
        var scope = MemoryScope.For(owner, item.GetProperty("includeShared").GetBoolean());
        var turns = item.GetProperty("turns").EnumerateArray().ToArray();
        await fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync("CREATE (:Conversation {id:'lifecycle-conversation', user_id:$owner})", new { owner });
            foreach (var turn in turns)
                await runner.RunAsync("MATCH (c:Conversation {id:'lifecycle-conversation'}) CREATE (m:Message {id:$id, conversation_id:'lifecycle-conversation', content:$text, session_id:'lifecycle'}), (c)-[:HAS_MESSAGE]->(m)",
                    new { id = Text(turn, "sourceId"), text = Text(turn, "text") });
        });
        using (var host = Host(item))
            foreach (var turn in turns)
            {
                using var turnScope = host.CreateScope(); var services = turnScope.ServiceProvider;
                var entity = item.GetProperty(Text(turn, "entityRef"));
                var input = new UnifiedExtractionResult
                {
                    Entities = [new ExtractedEntity { Name = Text(entity, "name"), Type = Text(entity, "type"), Subtype = entity.GetProperty("subtype").GetString(), Confidence = 1 }],
                    Facts = [.. turn.GetProperty("facts").EnumerateArray().Select(f => new ExtractedFact
                        { Subject = Text(f, "subject"), Predicate = Text(f, "predicate"), Object = Text(f, "object"), Confidence = 1 })],
                };
                var message = new Message { MessageId = Text(turn, "sourceId"), ConversationId = "lifecycle-conversation", SessionId = "lifecycle",
                    Role = "user", Content = Text(turn, "text"), TimestampUtc = DateTimeOffset.Parse(Text(turn, "timestampUtc")) };
                var staged = await services.GetRequiredService<IExtractionStage>().ProcessUnifiedAsync([message], input, ExtractionTypes.All, scope);
                await services.GetRequiredService<IPersistenceStage>().PersistAsync(staged, owner);
            }
        var decisions = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("MATCH (d:IdentityDecision {owner_id:$owner}) RETURN d.identity_record_json AS value ORDER BY d.created_at, d.id", new { owner });
            return (await cursor.ToListAsync()).Select(row => JsonSerializer.Deserialize<IdentityDecision>(ValueExtensions.As<string>(row["value"]))!).ToList();
        });
        var bindings = await fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync("""
                MATCH (f:Fact {owner_id:$owner})-[:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding)-[:DECIDED_BY]->(d:IdentityDecision), (b)-[:RESOLVES_TO]->(e:Entity)
                RETURN f.id AS fact, b.identity_fact_role AS role, e.id AS target, d.id AS decision
                """, new { owner });
            return (await cursor.ToListAsync()).Select(row => (ValueExtensions.As<string>(row["fact"]), ValueExtensions.As<string>(row["role"]),
                ValueExtensions.As<string>(row["target"]), ValueExtensions.As<string>(row["decision"]))).ToList();
        });
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        using (var reader = Host(item))
        {
            using var readScope = reader.CreateScope();
            var repository = readScope.ServiceProvider.GetRequiredService<IFactRepository>();
            foreach (var turn in turns)
                foreach (var f in turn.GetProperty("facts").EnumerateArray())
                {
                    var stored = (await repository.GetBySubjectAsync(Text(f, "subject"), scope))
                        .Where(x => x.Predicate == Text(f, "predicate") && x.Object == Text(f, "object")).ToList();
                    stored.Should().ContainSingle($"{Text(f, "key")} is written once with its original surface");
                    facts[Text(f, "key")] = stored[0];
                }
        }
        return new(decisions, bindings, facts);
    }

    private static string Identity(IdentityDecision decision) => decision.ConfirmedEntityId ?? decision.Observation.EntityId;

    [Theory]
    [MemberData(nameof(BindingCases))]
    public async Task Storage_sequential_identity_and_bindings(string id, string configuration, string variant, string check, string json)
    {
        _ = configuration; _ = variant; _ = check;
        using var document = JsonDocument.Parse(json); var item = document.RootElement;
        var outcome = await Run(item);
        var seedName = Text(item.GetProperty("seed"), "name"); var mentionName = Text(item.GetProperty("mention"), "name");
        var turns = item.GetProperty("turns").EnumerateArray().ToArray();
        IdentityDecision DecisionFor(JsonElement turn) => outcome.Decisions.Single(d => d.Context.SourceMessageIds.Contains(Text(turn, "sourceId")));
        var seed = DecisionFor(turns[0]); var mention = DecisionFor(turns[1]);
        seed.Observation.Name.Should().Be(seedName, id); mention.Observation.Name.Should().Be(mentionName, id);
        var expected = item.GetProperty("expected");
        mention.Status.Should().Be(Enum.Parse<IdentityDecisionKind>(Text(expected, "policyDecision")), id);
        if (expected.GetProperty("confirmedTarget").ValueKind == JsonValueKind.String)
            mention.ConfirmedEntityId.Should().Be(Identity(seed), $"{id}: the gold names the seed as the confirmed target");
        if (Text(item, "truth") == "different")
            Identity(mention).Should().NotBe(Identity(seed), $"{id}: different truth never shares an identity");
        foreach (var turn in turns)
        {
            var decision = DecisionFor(turn);
            foreach (var f in turn.GetProperty("facts").EnumerateArray())
            {
                var fact = outcome.FactsByKey[Text(f, "key")];
                outcome.Bindings.Should().Contain(b => b.FactId == fact.FactId && b.Role == Text(f, "entityRole") && b.Target == Identity(decision) && b.Decision == decision.DecisionId,
                    $"{id}: {Text(f, "key")} is bound to its own turn's decision and identity");
                outcome.Bindings.Where(b => b.FactId == fact.FactId).Should().OnlyContain(b => b.Target == Identity(decision),
                    $"{id}: {Text(f, "key")} is never bound to another identity");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReadCases))]
    public async Task Retrieval_fresh_host_read_of_required_facts(string id, string configuration, string variant, string check, string json)
    {
        _ = configuration; _ = variant; _ = check;
        using var document = JsonDocument.Parse(json); var item = document.RootElement;
        var outcome = await Run(item);
        var retrieval = item.GetProperty("retrieval");
        foreach (var key in retrieval.GetProperty("requiredFactKeys").EnumerateArray().Select(k => k.GetString()!))
            outcome.FactsByKey.Should().ContainKey(key, $"{id}: a fresh host reads {key}");
        foreach (var key in retrieval.GetProperty("excludedFactKeys").EnumerateArray().Select(k => k.GetString()!))
            outcome.FactsByKey.Should().NotContainKey(key, id);
        using var reader = Host(item); using var readScope = reader.CreateScope();
        var repository = readScope.ServiceProvider.GetRequiredService<IFactRepository>();
        foreach (var fact in outcome.FactsByKey.Values)
            (await repository.GetBySubjectAsync(fact.Subject, MemoryScope.For("another-owner", false)))
                .Should().NotContain(other => other.FactId == fact.FactId, $"{id}: another owner never reads the pair's facts");
    }
}
