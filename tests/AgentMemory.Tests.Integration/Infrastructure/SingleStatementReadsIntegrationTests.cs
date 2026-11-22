using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Infrastructure;

/// <summary>
/// DRT-6 (C-01), check K-5: every read moved to the single-statement path sends exactly one statement, on the paths it
/// was moved for (recall, recall as of a moment, the profile block's rebuild, saving a fact and a preference), and
/// recall finds the same items with the path on and off.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SingleStatementReadsIntegrationTests(Neo4jIntegrationFixture fixture) : IAsyncLifetime
{
    private const string Owner = "k5-owner";
    private const string Session = "k5-session";

    public Task InitializeAsync() => fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider Build(bool singleStatementReads, MemoryOptions? preset = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        void Neo4j(Neo4jOptions o)
        {
            o.Uri = fixture.ConnectionString;
            o.Username = fixture.User;
            o.Password = fixture.Password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            o.SingleStatementReads = singleStatementReads;
        }
        if (preset is null) services.AddNeo4jAgentMemory(configureMemory: _ => { }, configureNeo4j: Neo4j);
        else services.AddNeo4jAgentMemory(preset, Neo4j);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task SeedAsync(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var memory = services.GetRequiredService<IMemoryService>();
        await memory.AddMessageAsync(Session, "k5-conversation", "user", "I moved to Basel and I prefer tea in the morning.");
        await memory.AddMessageAsync(Session, "k5-conversation", "assistant", "Noted: Basel, and tea in the morning.");
        var longTerm = services.GetRequiredService<ILongTermMemoryService>();
        await longTerm.AddEntityAsync(new Entity { EntityId = "k5-basel", Name = "Basel", Type = "Location", Confidence = 0.9, CreatedAtUtc = now, OwnerId = Owner });
        await longTerm.AddFactAsync(new Fact { FactId = "k5-lives", Subject = "user", Predicate = "lives in", Object = "Basel", Confidence = 0.9, CreatedAtUtc = now, OwnerId = Owner });
        await longTerm.AddPreferenceAsync(new Preference { PreferenceId = "k5-tea", Category = "drink", PreferenceText = "Tea in the morning", Confidence = 0.9, CreatedAtUtc = now, OwnerId = Owner });
    }

    private static RecallRequest Recall() => new() { SessionId = Session, UserId = Owner, Query = "Where do I live and what do I drink?" };

    [Fact]
    public async Task EachMovedRead_SendsOneStatement_AndRecallFindsTheSameItemsEitherWay()
    {
        await using var onRoot = Build(singleStatementReads: true);
        await using var onScope = onRoot.CreateAsyncScope();
        var on = onScope.ServiceProvider;
        await SeedAsync(on);

        var (transactions, queries, _) = await TraceAsync(async () =>
        {
            var assembler = on.GetRequiredService<IMemoryContextAssembler>();
            var live = await assembler.AssembleContextAsync(Recall());
            await assembler.AssembleContextAsOfAsync(Recall(), DateTimeOffset.UtcNow);
            await on.GetRequiredService<IWorkingMemoryService>().RebuildAsync(Owner);
            // The duplicate check before a save is one of the moved reads.
            var longTerm = on.GetRequiredService<ILongTermMemoryService>();
            await longTerm.AddFactAsync(new Fact { FactId = "k5-works", Subject = "user", Predicate = "works in", Object = "Basel", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = Owner });
            await longTerm.AddPreferenceAsync(new Preference { PreferenceId = "k5-coffee", Category = "drink", PreferenceText = "Coffee after lunch", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = Owner });
            return live;
        });

        var single = transactions.Where(t => t.GetTagItem("db.single_statement") is true).ToList();
        single.Should().HaveCountGreaterThanOrEqualTo(10, "recall, recall as of a moment and the rebuild all read through the path");
        foreach (var tx in single)
        {
            var sent = queries.Where(q => q.ParentSpanId == tx.SpanId).Select(q => q.GetTagItem("db.query.fingerprint") as string).ToList();
            sent.Should().ContainSingle($"a single-statement read sends one statement (sent: {string.Join(", ", sent)})");
        }

        await using var offRoot = Build(singleStatementReads: false);
        await using var offScope = offRoot.CreateAsyncScope();
        var off = offScope.ServiceProvider;
        // The same data, recalled on both paths.
        var context = await on.GetRequiredService<IMemoryContextAssembler>().AssembleContextAsync(Recall());
        var (offTransactions, _, offContext) = await TraceAsync(() => off.GetRequiredService<IMemoryContextAssembler>().AssembleContextAsync(Recall()));
        offTransactions.Should().NotContain(t => Equals(t.GetTagItem("db.single_statement"), true), "switched off, every read is a managed transaction");
        Ids(context).Should().Equal(Ids(offContext));
        Ids(context).Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheConversationalPresetsMovedReads_SendOneStatementEach()
    {
        // The preset's recall adds the reads the defaults leave out: due facts, the look-ahead, person and event
        // details, relationships, and the searches of a question that fans out.
        await using var root = Build(singleStatementReads: true, MemoryOptions.CreateConversational());
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await SeedAsync(services);

        var (transactions, queries, _) = await TraceAsync(async () =>
        {
            var assembler = services.GetRequiredService<IMemoryContextAssembler>();
            await assembler.AssembleContextAsync(new RecallRequest
            {
                SessionId = Session, UserId = Owner, Query = "What did I plan for next week, and where do I live?",
            });
            return 0;
        });

        var single = transactions.Where(t => t.GetTagItem("db.single_statement") is true).ToList();
        single.Should().NotBeEmpty();
        foreach (var tx in single)
        {
            var sent = queries.Where(q => q.ParentSpanId == tx.SpanId).Select(q => q.GetTagItem("db.query.fingerprint") as string).ToList();
            sent.Should().ContainSingle($"a single-statement read sends one statement (sent: {string.Join(", ", sent)})");
        }
    }

    private static IReadOnlyList<string> Ids(MemoryContext context) =>
        [.. context.RelevantFacts.Items.Select(f => "fact:" + f.FactId),
         .. context.RelevantPreferences.Items.Select(p => "preference:" + p.PreferenceId),
         .. context.RelevantEntities.Items.Select(e => "entity:" + e.EntityId),
         .. context.RecentMessages.Items.Select(m => "message:" + m.MessageId)];

    private static async Task<(List<Activity> Transactions, List<Activity> Queries, T Result)> TraceAsync<T>(Func<Task<T>> work)
    {
        var transactions = new List<Activity>();
        var queries = new List<Activity>();
        var trace = new ActivityTraceId[1];
        using var listener = new ActivityListener
        {
            // Scoped to this test's trace: the listener is process-global.
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != trace[0]) return;
                lock (transactions)
                {
                    if (activity.OperationName == "memory.db.tx") transactions.Add(activity);
                    else if (activity.OperationName == "memory.db.query") queries.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        T result;
        using (var root = AgentMemoryDiagnostics.Source.StartActivity("test.k5"))
        {
            trace[0] = root!.TraceId;
            result = await work();
        }
        lock (transactions) return ([.. transactions], [.. queries], result);
    }
}
