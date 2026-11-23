using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    /// <summary>
    /// DRT-0 (W0.5): operations the round-trip findings measured that the default catalog does not cover. Not in the
    /// default run (the CI gate's catalog is unchanged): select them by id, with <c>--db-latency 20</c> to see waiting.
    /// </summary>
    // A property, not a field: All (in another part of this class) reads it while the type initializes.
    internal static PerfScenario[] RoundTripScenarios =>
    [
        new(
            "PERF-D-01",
            "One fact saved through the API (the profile block is rebuilt after it)",
            SaveFactThroughApiAsync,
            SupportsInterleavedAb: false,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-02",
            "One reasoning step with one tool call, inside a trace started before the turn",
            RecordStepWithToolCallAsync,
            SupportsInterleavedAb: false,
            SetupAsync: StartTraceForStepAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-03",
            "Start-up on an existing store: the schema bootstrap with its migrations, everything already in place",
            ctx => ctx.Profile.Services.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(ctx.CancellationToken),
            SupportsInterleavedAb: false,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-04",
            $"The read-note queue: {QueuedRecalls} recalls' read notes (10 facts, 10 entities, 5 preferences each) tracked at once, then flushed",
            ctx => DrainReadNotesAsync(ctx, oneWritePerRecall: false),
            SupportsInterleavedAb: false,
            SetupAsync: PickRecalledItemsAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-05",
            $"PERF-D-04 with one write per recall, as before DRT-5 (the AppContext switch AgentMemory.AccessTracking.OneWritePerRecall)",
            ctx => DrainReadNotesAsync(ctx, oneWritePerRecall: true),
            SupportsInterleavedAb: false,
            SetupAsync: PickRecalledItemsAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-06",
            "The first recall of a new process: a fresh product (its own connection pool), the schema bootstrap, a second's pause, then recall at the defaults",
            ctx => FirstRecallOfANewProcessAsync(ctx, "PERF-D-06"),
            SupportsInterleavedAb: false,
            SetupAsync: StartFreshProductAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-07",
            "Recall at the defaults after a pause of 31 seconds (the cached owner counts are past their 30 seconds)",
            ctx => RecallAfterAPauseAsync(ctx, "PERF-D-07", countInLine: false),
            SupportsInterleavedAb: false,
            SetupAsync: PauseAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-08",
            "PERF-D-07 with the count made before the search, as before DRT-PAUSE (the AppContext switch AgentMemory.Recall.OwnerCountsInLine)",
            ctx => RecallAfterAPauseAsync(ctx, "PERF-D-08", countInLine: true),
            SupportsInterleavedAb: false,
            SetupAsync: PauseAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-09",
            "Recall of a two-part question with fan-out on (the Conversational preset's planner, the deterministic deriver)",
            ctx => FanOutRecallAsync(ctx, "PERF-D-09", inTurn: false),
            SupportsInterleavedAb: false,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-10",
            "PERF-D-09 with the fan-out's searches one at a time, as before DRT-7 (the AppContext switch AgentMemory.Recall.FanOutSearchesInTurn)",
            ctx => FanOutRecallAsync(ctx, "PERF-D-10", inTurn: true),
            SupportsInterleavedAb: false,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-11",
            $"Recall for people on a large owner: {LargePeopleOwnerCount:N0} people, a question naming one of them, recall for people on",
            ctx => PeopleRecallAsync(ctx, "PERF-D-11", LargePeopleOwner, personDetails: true),
            SupportsInterleavedAb: false,
            SetupAsync: SeedPeopleOwnersAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-12",
            "PERF-D-11 with recall for people off: what the people read adds",
            ctx => PeopleRecallAsync(ctx, "PERF-D-12", LargePeopleOwner, personDetails: false),
            SupportsInterleavedAb: false,
            SetupAsync: SeedPeopleOwnersAsync,
            IncludeInDefaultRun: false),
        new(
            "PERF-D-13",
            $"Recall for people on an owner of {SmallPeopleOwnerCount} people, a question naming one of them: all three reads",
            ctx => PeopleRecallAsync(ctx, "PERF-D-13", SmallPeopleOwner, personDetails: true),
            SupportsInterleavedAb: false,
            SetupAsync: SeedPeopleOwnersAsync,
            IncludeInDefaultRun: false),
    ];

    // P231-A part 4: the people read on a large owner. Two owners of their own, so no other scenario's recall sees them.
    private const string LargePeopleOwner = "perf-people-large";
    private const string SmallPeopleOwner = "perf-people-small";
    private const int LargePeopleOwnerCount = 20_000;
    private const int SmallPeopleOwnerCount = 200;
    private static readonly SemaphoreSlim PeopleSeeded = new(1, 1);
    private static bool _peopleSeeded;

    // Recall for people needs the identity-evidence extension, enabled and migrated: a product of its own on the same
    // store, as a host with identity on runs it. Kept for the run (the process ends with it).
    private static ServiceProvider? _identityProduct;

    /// <summary>
    /// Seeds, once per run, the two owners' people as identity leaves them: live PERSON entities with the names their
    /// bound facts used (<c>identity_surfaces</c>), each with a full name and a short form, and one fact each bound to
    /// them through a decided identity (so each person is confirmed, and the third read has facts to read). Not measured.
    /// </summary>
    private static async Task SeedPeopleOwnersAsync(ScenarioSetupContext ctx)
    {
        await PeopleSeeded.WaitAsync(ctx.CancellationToken).ConfigureAwait(false);
        try
        {
            if (_peopleSeeded) return;
            var product = ctx.Profile.BuildFreshProduct(neo4j => neo4j.Extensions.Add("identity-evidence"));
            await product.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(ctx.CancellationToken).ConfigureAwait(false);
            _identityProduct = product;
            var driver = ctx.Profile.Services.GetRequiredService<IDriver>();
            var database = ctx.Profile.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Neo4jOptions>>().Value.Database;
            foreach (var (owner, count) in new[] { (LargePeopleOwner, LargePeopleOwnerCount), (SmallPeopleOwner, SmallPeopleOwnerCount) })
            {
                await driver.ExecutableQuery("""
                        UNWIND range(1, $count) AS i
                        WITH i, 'Person ' + toString(i) + ' Example' AS name, $owner + '-' + toString(i) AS key
                        CREATE (p:Entity {id: key + '-person', owner_id: $owner, owner_key: $owner,
                                          type: 'PERSON', name: name, canonical_name: toLower(name), aliases: [],
                                          identity_observation_key: key, identity_surfaces: [name, 'Person ' + toString(i)],
                                          confidence: 0.9, created_at: datetime(), updated_at: datetime()})
                        CREATE (d:IdentityDecision {id: key + '-decision', owner_id: $owner, owner_scope: 'owned:' + $owner,
                                                    identity_observation_key: key, identity_revision: 1, identity_lock_version: 1,
                                                    created_at: datetime()})
                        CREATE (f:Fact {id: key + '-fact', owner_id: $owner, owner_key: $owner, subject: name,
                                        predicate: 'works at', object: 'Company ' + toString(i), confidence: 0.9,
                                        created_at: datetime(), updated_at: datetime()})
                        CREATE (b:IdentityFactBinding {id: key + '-binding', owner_id: $owner, owner_scope: 'owned:' + $owner,
                                                       identity_decision_id: key + '-decision', identity_observed_surface: name})
                        CREATE (f)-[:HAS_IDENTITY_BINDING]->(b), (b)-[:DECIDED_BY]->(d), (b)-[:RESOLVES_TO]->(p)
                        """)
                    .WithParameters(new { owner, count })
                    .WithConfig(new QueryConfig(database: database))
                    .ExecuteAsync(ctx.CancellationToken).ConfigureAwait(false);
            }
            _peopleSeeded = true;
        }
        finally
        {
            PeopleSeeded.Release();
        }
    }

    /// <summary>
    /// One recall for an owner of many people, through the assembler with recall for people set on or off, asking about
    /// a person by name. Fails unless the people reads ran as their path says: on a large owner the light read only (a
    /// full page of people names nobody), on a small one all three; off, none.
    /// </summary>
    private static async Task PeopleRecallAsync(ScenarioContext ctx, string scenarioId, string owner, bool personDetails)
    {
        var product = _identityProduct ?? throw new InvalidOperationException($"{scenarioId}: the identity product was not started");
        await using var scope = product.CreateAsyncScope();
        var assembler = scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>();
        await assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = owner + "-session",
            UserId = owner,
            Query = "What do I know about Person 150 Example?",
            Options = AgentMemory.Abstractions.Options.RecallOptions.Default with { PersonDetails = personDetails },
        }, ctx.CancellationToken).ConfigureAwait(false);

        int Spans(string name) => ctx.Turn.SpanCounts.TryGetValue(name, out var count) ? (int)count : 0;
        var (names, identities, details) = (Spans("memory.recall.person-names"), Spans("memory.recall.person-identities"),
            Spans("memory.recall.person-details"));
        // On the large owner the light read stops at its bound of people; the second read runs only when the named person
        // is among them, and then comes back a full page, which names nobody (REV4-1).
        var ran = !personDetails ? (names, identities, details) == (0, 0, 0)
            : owner == LargePeopleOwner ? names == 1 && identities <= 1 && details == 0
            : (names, identities, details) == (1, 1, 1);
        if (!ran)
            throw new InvalidOperationException(
                $"{scenarioId}: the people reads ran as names/identities/details {names}/{identities}/{details}; the " +
                "measurement would not be of the people read.");
    }

    /// <summary>
    /// DRT-7: recall with fan-out on, through an assembler built as the container builds it but with the planner enabled
    /// and the registered deriver, on a question of two parts. Fails unless fan-out fired with every leg, so the
    /// measurement cannot be of a fan-out that did nothing.
    /// </summary>
    private static async Task FanOutRecallAsync(ScenarioContext ctx, string scenarioId, bool inTurn)
    {
        var services = ctx.Profile.Services;
        var configured = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentMemory.Abstractions.Options.MemoryOptions>>().Value;
        var options = configured with { FanOut = new AgentMemory.Abstractions.Options.RecallFanOutOptions { Enabled = true } };
        var assembler = new AgentMemory.Core.Services.MemoryContextAssembler(
            services.GetRequiredService<IShortTermMemoryService>(), services.GetRequiredService<ILongTermMemoryService>(),
            services.GetRequiredService<IReasoningMemoryService>(), null, services.GetRequiredService<IEmbeddingOrchestrator>(),
            services.GetRequiredService<TimeProvider>(), Microsoft.Extensions.Options.Options.Create(options),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AgentMemory.Core.Services.MemoryContextAssembler>>(),
            services.GetRequiredService<AgentMemory.Abstractions.Services.IMemoryIsolationPolicy>(), null, null,
            subQueryDeriver: services.GetRequiredService<AgentMemory.Core.Services.ISubQueryDeriver>());
        var identity = PerfFixture.DefaultIdentity;
        AppContext.SetSwitch("AgentMemory.Recall.FanOutSearchesInTurn", inTurn);
        try
        {
            var context = await assembler.AssembleContextAsync(new RecallRequest
            {
                SessionId = identity.SessionId, UserId = identity.OwnerId,
                // Two question words (the gate's rule C1), so the planner splits it.
                Query = $"What do I know about {PerfFixture.ProbeQueryFor(identity).TrimEnd('?', '.')}, and where do I prefer to work?",
                Options = ctx.RecallOptions,
            }, ctx.CancellationToken).ConfigureAwait(false);
            if (context.FanOutReport is not { GateFired: true } report || report.VoidReason is not null || report.SubQueries.Count < 2)
                throw new InvalidOperationException($"{scenarioId}: fan-out did not fire with two or more legs (fired {context.FanOutReport?.GateFired}, void {context.FanOutReport?.VoidReason ?? "none"}, legs {context.FanOutReport?.SubQueries.Count ?? 0})");
        }
        finally
        {
            AppContext.SetSwitch("AgentMemory.Recall.FanOutSearchesInTurn", false);
        }
    }

    /// <summary>DRT-PAUSE: the person reads the answer and types the next message. Not measured.</summary>
    private static Task PauseAsync(ScenarioSetupContext ctx) => Task.Delay(TimeSpan.FromSeconds(31), ctx.CancellationToken);

    private static async Task RecallAfterAPauseAsync(ScenarioContext ctx, string scenarioId, bool countInLine)
    {
        AppContext.SetSwitch("AgentMemory.Recall.OwnerCountsInLine", countInLine);
        try
        {
            await RunDefaultRecallAsync(ctx, scenarioId).ConfigureAwait(false);
        }
        finally
        {
            AppContext.SetSwitch("AgentMemory.Recall.OwnerCountsInLine", false);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ServiceProvider> FreshProducts = new();

    /// <summary>
    /// DRT-WARM: a new process, as a host starts one: its own services and connection pool, the schema bootstrap, then
    /// the pause before the person's first message (a second). Not measured: only the recall is.
    /// </summary>
    private static async Task StartFreshProductAsync(ScenarioSetupContext ctx)
    {
        var product = ctx.Profile.BuildFreshProduct();
        await product.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(ctx.CancellationToken).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), ctx.CancellationToken).ConfigureAwait(false);
        FreshProducts[$"{ctx.Phase}/{ctx.Iteration}"] = product;
    }

    private static async Task FirstRecallOfANewProcessAsync(ScenarioContext ctx, string scenarioId)
    {
        if (!FreshProducts.TryRemove($"{ctx.Phase}/{ctx.Iteration}", out var product))
            throw new InvalidOperationException($"{scenarioId}: no fresh product was started for this iteration");
        try
        {
            await using var scope = product.CreateAsyncScope();
            await RunDefaultRecallAsync(ctx, scenarioId, CreateProvider(scope.ServiceProvider, ctx.RecallOptions)).ConfigureAwait(false);
        }
        finally
        {
            await product.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>DRT-5: how many recalls one PERF-D-04 iteration hands to the queue.</summary>
    private const int QueuedRecalls = 100;

    private static IReadOnlyList<(string NodeId, MemoryNodeKind Kind)>? _recalledItems;

    /// <summary>Picks, once, 25 of the owner's stored items, the shape of one default recall.</summary>
    private static async Task PickRecalledItemsAsync(ScenarioSetupContext ctx)
    {
        if (_recalledItems is not null) return;
        var driver = ctx.Profile.Services.GetRequiredService<IDriver>();
        var items = new List<(string, MemoryNodeKind)>();
        foreach (var (label, kind, count) in new[] { ("Fact", MemoryNodeKind.Fact, 10), ("Entity", MemoryNodeKind.Entity, 10), ("Preference", MemoryNodeKind.Preference, 5) })
        {
            var result = await driver.ExecutableQuery($"MATCH (n:{label}) RETURN n.id AS id ORDER BY n.id LIMIT {count}")
                .WithConfig(new QueryConfig(database: ctx.Profile.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Neo4jOptions>>().Value.Database))
                .ExecuteAsync(ctx.CancellationToken).ConfigureAwait(false);
            items.AddRange(result.Result.Select(r => (r["id"].As<string>(), kind)));
        }
        if (items.Count == 0) throw new InvalidOperationException("PERF-D-04: the store holds no facts, entities or preferences to track");
        _recalledItems = items;
    }

    private static async Task DrainReadNotesAsync(ScenarioContext ctx, bool oneWritePerRecall)
    {
        var tracker = ctx.Profile.Services.GetRequiredService<IMemoryAccessTracker>();
        AppContext.SetSwitch("AgentMemory.AccessTracking.OneWritePerRecall", oneWritePerRecall);
        try
        {
            for (var i = 0; i < QueuedRecalls; i++) tracker.Track(_recalledItems!);
            await tracker.FlushAsync(ctx.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            AppContext.SetSwitch("AgentMemory.AccessTracking.OneWritePerRecall", false);
        }
    }

    private static async Task SaveFactThroughApiAsync(ScenarioContext ctx)
    {
        var unique = $"{ctx.Phase}-{ctx.Iteration}-{Guid.NewGuid():N}";
        var saved = await ctx.Profile.Services.GetRequiredService<ILongTermMemoryService>().AddFactAsync(new Fact
        {
            FactId = $"perf-d01-{unique}",
            // A subject of its own: the duplicate search still runs, but cannot match an earlier probe whose hashed
            // embedding happens to land within the dedup threshold (it did once in 26 runs, and the fact came back as
            // that earlier one).
            Subject = $"Perf probe {unique}",
            Predicate = "noted",
            Object = $"a round-trip probe {unique}",
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            OwnerId = PerfFixture.OwnerId,
        }, ctx.CancellationToken).ConfigureAwait(false);
        if (saved.FactId != $"perf-d01-{unique}")
            throw new InvalidOperationException($"PERF-D-01: the fact came back as {saved.FactId}");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TracesForSteps = new();

    private static async Task StartTraceForStepAsync(ScenarioSetupContext ctx)
    {
        var trace = await ctx.Profile.Services.GetRequiredService<IReasoningMemoryService>().StartTraceAsync(
            "perf-d02-session", $"perf step probe {ctx.Phase} {ctx.Iteration}", ownerId: PerfFixture.OwnerId,
            cancellationToken: ctx.CancellationToken).ConfigureAwait(false);
        TracesForSteps[$"{ctx.Phase}/{ctx.Iteration}"] = trace.TraceId;
    }

    private static async Task RecordStepWithToolCallAsync(ScenarioContext ctx)
    {
        var reasoning = ctx.Profile.Services.GetRequiredService<IReasoningMemoryService>();
        var step = await reasoning.AddStepAsync(TracesForSteps[$"{ctx.Phase}/{ctx.Iteration}"], 1, thought: "check",
            action: "search", observation: "found", cancellationToken: ctx.CancellationToken).ConfigureAwait(false);
        await reasoning.RecordToolCallAsync(step.StepId, "perf_tool", "{\"q\":1}", "{\"r\":1}", ToolCallStatus.Success, 5,
            cancellationToken: ctx.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>On a connection the pool already holds: a new session's auto-commit query (RESET, then RUN and PULL).</summary>
    public const int AutoCommitRoundTrips = 2;

    /// <summary>On a connection the pool already holds: a new session's managed transaction of one query (RESET, BEGIN, RUN and PULL, COMMIT).</summary>
    public const int ManagedTransactionRoundTrips = 4;

    /// <summary>
    /// DRT-0 (W0.4): before any scenario, the link proxy counts the driver's two basic shapes through the product's own
    /// driver. If a driver upgrade changes how many round trips either costs, every count after it means something
    /// else, so the run stops here and says so. With a delay, it also measures what one delayed round trip really
    /// costs (the sleep and the scheduler add a little to the nominal delay), from auto-commit queries with and without
    /// it, and returns that, so waiting is divided by what was really added.
    /// </summary>
    public static async Task<TimeSpan> RoundTripSelfCheckAsync(HermeticProfile profile, TextWriter log, TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var link = profile.Link ?? throw new InvalidOperationException("the run has no link proxy (--db-latency)");
        var driver = profile.Services.GetRequiredService<IDriver>();

        async Task<long> CountAsync(Func<IAsyncSession, Task> work, bool measure)
        {
            var before = link.Counter.Count;
            link.Measuring = measure;
            try
            {
                await using var session = driver.AsyncSession(o => o.WithDatabase("neo4j"));
                await work(session).ConfigureAwait(false);
            }
            finally
            {
                link.Measuring = false;
            }
            return link.Counter.Count - before;
        }

        static async Task AutoCommit(IAsyncSession session)
        {
            var cursor = await session.RunAsync("RETURN 1 AS one").ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        }

        static Task Managed(IAsyncSession session) => session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("RETURN 1 AS one").ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        });

        cancellationToken.ThrowIfCancellationRequested();
        await CountAsync(AutoCommit, measure: false).ConfigureAwait(false);   // the pool holds a connection from here on
        var autoCommit = await CountAsync(AutoCommit, measure: true).ConfigureAwait(false);
        var managed = await CountAsync(Managed, measure: true).ConfigureAwait(false);
        log.WriteLine($"perf: link self-check: an auto-commit query {autoCommit} round trips, a managed transaction {managed}");
        if (autoCommit != AutoCommitRoundTrips || managed != ManagedTransactionRoundTrips)
            throw new InvalidOperationException(
                $"an auto-commit query took {autoCommit} round trips (expected {AutoCommitRoundTrips}) and a managed " +
                $"transaction {managed} (expected {ManagedTransactionRoundTrips}); the driver changed how it talks to " +
                "the store, so the round-trip counts of this run would not compare with earlier ones");
        if (delay <= TimeSpan.Zero) return delay;

        async Task<double> MedianMsAsync(TimeSpan roundTrip)
        {
            var samples = new List<double>();
            for (var i = 0; i < 15; i++)
            {
                link.RoundTrip = roundTrip;
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                await CountAsync(AutoCommit, measure: true).ConfigureAwait(false);
                samples.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            link.RoundTrip = TimeSpan.Zero;
            samples.Sort();
            return samples[samples.Count / 2];
        }

        var near = await MedianMsAsync(TimeSpan.Zero).ConfigureAwait(false);
        var far = await MedianMsAsync(delay).ConfigureAwait(false);
        var real = TimeSpan.FromMilliseconds((far - near) / AutoCommitRoundTrips);
        log.WriteLine($"perf: link calibration: a round trip set to {delay.TotalMilliseconds:0} ms costs {real.TotalMilliseconds:0.00} ms");
        if (real < delay * 0.9 || real > delay * 1.5)
            throw new InvalidOperationException(
                $"a round trip set to {delay.TotalMilliseconds:0} ms cost {real.TotalMilliseconds:0.00} ms: this machine " +
                "cannot hold the delay closely enough for the waiting to mean anything");
        return real;
    }
}
