using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spike0.Host;

// Spike 0's prototype host (crosslang-architecture.md §5 Step 0), executed under the private-demo
// carve-out. THROWAWAY BY DESIGN: it exists to answer one question cheaply — can a wire carry a .NET
// recall result such that a non-.NET client reconstructs the same answer? — and a "no" here ends the
// spike with a written finding instead of a contract package nobody can honour.
//
// It references the shipped packages and changes nothing in them. Zero diff to src/.

var builder = WebApplication.CreateBuilder(args);

var uri = builder.Configuration["Neo4j:Uri"]
    ?? Environment.GetEnvironmentVariable("NEO4J_URI")
    ?? "bolt://localhost:7687";
var user = builder.Configuration["Neo4j:Username"]
    ?? Environment.GetEnvironmentVariable("NEO4J_USERNAME")
    ?? "neo4j";
var password = builder.Configuration["Neo4j:Password"]
    ?? Environment.GetEnvironmentVariable("NEO4J_PASSWORD")
    ?? "neo4j";
const int Dimensions = 8;

builder.Services.AddNeo4jAgentMemory(
    _ => { },
    o =>
    {
        o.Uri = uri;
        o.Username = user;
        o.Password = password;
        o.EmbeddingDimensions = Dimensions;
    });

// The same deterministic stand-in the CLI uses. A real provider would make the parity script's two
// reads disagree for reasons that have nothing to do with the wire, which is the one confound this
// spike cannot afford.
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new StubEmbeddingGenerator(
        sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Dimensions));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.WriteIndented = false;
});

var app = builder.Build();

// Bootstrap the schema before serving. A prototype host pointed at an empty database otherwise fails
// its first recall with "no such vector schema index" -- which reads as a wire problem and is not one.
// Doing it at startup rather than inside /v1/spike/seed means a recall cannot reach an unprepared
// database by any route, including a caller who skips seeding.
//
// A real server would NOT do this: application startup running DDL against a shared database is the
// thing `agentmemory migrate` exists to keep out of the request path. It is right here only because
// this host owns its throwaway database completely.
using (var startupScope = app.Services.CreateScope())
{
    await startupScope.ServiceProvider
        .GetRequiredService<ISchemaBootstrapper>()
        .BootstrapAsync(CancellationToken.None);
}

// ── meta ──────────────────────────────────────────────────────────────

app.MapGet("/v1/meta", () => Results.Ok(new
{
    wire = "am-wire/1-draft",
    stage = "spike0",
    // Stated on the endpoint itself, because the one thing a prototype must never do is look
    // production-ready to someone who found it by port-scanning.
    warning = "PROTOTYPE — throwaway spike host. The productized SDK follows the published designs.",
    capabilities = new[] { "recall", "recall.asOf", "isolation.owner" },
}));

// ── seeding ───────────────────────────────────────────────────────────

app.MapPost("/v1/spike/seed", async (
    ILongTermMemoryService longTerm,
    IFactRepository facts,
    INeo4jTransactionRunner tx,
    CancellationToken ct) =>
{
    // Cleared first, so a re-run is idempotent and a diff between runs means a real change rather than
    // accumulated state from the previous one.
    await tx.WriteAsync(async runner =>
    {
        await runner.RunAsync("MATCH (n) WHERE n.id STARTS WITH 'spike0-' DETACH DELETE n");
    }, ct);

    foreach (var fact in Fixtures.All)
        await longTerm.AddFactAsync(fact, ct);

    // Applied through the repository so the graph carries a real SUPERSEDED_BY edge. Hand-setting
    // invalidated_at would produce a shape the engine never produces, and the spike would then be
    // testing the wire against a fiction.
    await facts.SupersedeAsync(
        Fixtures.SupersessionLoserId,
        Fixtures.SupersessionWinnerId,
        MemoryScope.For(Fixtures.Alice, includeShared: false),
        ct);

    return Results.Ok(new { seeded = Fixtures.All.Count() });
});

// ── recall ────────────────────────────────────────────────────────────

static RecallRequest ToRequest(WireRecallRequest wire)
{
    var options = RecallOptions.Default with
    {
        MaxFacts = wire.MaxFacts ?? RecallOptions.Default.MaxFacts,
        MaxEntities = wire.MaxEntities ?? RecallOptions.Default.MaxEntities,
        MaxPreferences = wire.MaxPreferences ?? RecallOptions.Default.MaxPreferences,
        // Floor 0 by default, which is NOT what a product would ship and is right here.
        //
        // This host runs on StubEmbeddingGenerator, whose vectors are deterministic but carry no
        // semantic relationship — so cosine similarity between a query and a fact is essentially noise
        // and nothing clears the shipped 0.7 floor. The first run of this spike passed all five
        // fixtures with ZERO facts on both sides: empty compared to empty, which proves nothing about
        // a wire.
        //
        // Spike 0 asks whether the wire can carry a recall result, not whether retrieval ranks well.
        // Dropping the floor makes the vector search return the owner's top-K regardless of score, so
        // the fixtures contain something to compare. Retrieval quality is measured elsewhere, with a
        // real embedding provider, and is not this spike's question.
        MinSimilarityScore = wire.MinSimilarityScore ?? 0.0,
        // Recent messages are off: this spike seeds no conversation, and leaving them on would put a
        // section in the response that neither path can populate, which proves nothing either way.
        MaxRecentMessages = 0,
        MaxRelevantMessages = 0,
        MaxTraces = 0,
    };

    return new RecallRequest
    {
        SessionId = wire.SessionId,
        UserId = wire.UserId,
        Query = wire.Query,
        Options = options,
    };
}

static async Task<RecallResult> ExecuteAsync(
    IMemoryService memory, WireRecallRequest wire, CancellationToken ct)
{
    var request = ToRequest(wire);
    return wire.AsOf is { } asOf
        ? await memory.RecallAsOfAsync(request, asOf, wire.SystemAsOf, ct)
        : await memory.RecallAsync(request, ct);
}

app.MapPost("/v1/recall", async (
    WireRecallRequest wire, IMemoryService memory, CancellationToken ct) =>
{
    var result = await ExecuteAsync(memory, wire, ct);
    var context = result.Context;

    return Results.Ok(new WireRecallResponse
    {
        TotalItemsRetrieved = result.TotalItemsRetrieved,
        Truncated = result.Truncated,
        Facts = [.. context.RelevantFacts.Items.Select(f => new WireFact
        {
            Id = f.FactId,
            Subject = f.Subject,
            Predicate = f.Predicate,
            Object = f.Object,
            Confidence = f.Confidence,
            ValidFrom = f.ValidFrom,
            ValidUntil = f.ValidUntil,
            OwnerId = f.OwnerId,
        })],
        Entities = [.. context.RelevantEntities.Items.Select(e => new WireEntity
        {
            Id = e.EntityId, Name = e.Name, Type = e.Type, OwnerId = e.OwnerId,
        })],
        Preferences = [.. context.RelevantPreferences.Items.Select(p => new WirePreference
        {
            Id = p.PreferenceId, Category = p.Category, Text = p.PreferenceText, OwnerId = p.OwnerId,
        })],
    });
});

// ── the direct path, for comparison ───────────────────────────────────

// Returns the CANONICAL projection built from the domain result, in-process. The parity script builds
// the same projection from the wire JSON, in Python. Comparing those two is what asks the spike's real
// question: is the wire JSON alone sufficient to reconstruct the .NET answer? A field the DTO drops is
// a field Python cannot produce, and the compare fails.
app.MapPost("/v1/spike/recall-direct", async (
    WireRecallRequest wire, IMemoryService memory, CancellationToken ct) =>
{
    var result = await ExecuteAsync(memory, wire, ct);
    var context = result.Context;

    static string Stamp(DateTimeOffset? value) => value is { } v
        ? v.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
        : "-";

    return Results.Ok(new CanonicalRecall
    {
        TotalItemsRetrieved = result.TotalItemsRetrieved,
        Truncated = result.Truncated,
        // One string per item, sorted. Sorting removes ordering as a source of false differences --
        // ranking order is a separate question from whether the wire carries the content, and
        // conflating them would make this fixture fail for the wrong reason.
        Facts = [.. context.RelevantFacts.Items
            .Select(f => string.Join('|',
                f.FactId, f.Subject, f.Predicate, f.Object,
                f.Confidence.ToString("0.####", CultureInfo.InvariantCulture),
                Stamp(f.ValidFrom), Stamp(f.ValidUntil), f.OwnerId ?? "-"))
            .OrderBy(s => s, StringComparer.Ordinal)],
        Entities = [.. context.RelevantEntities.Items
            .Select(e => string.Join('|', e.EntityId, e.Name, e.Type, e.OwnerId ?? "-"))
            .OrderBy(s => s, StringComparer.Ordinal)],
        Preferences = [.. context.RelevantPreferences.Items
            .Select(p => string.Join('|', p.PreferenceId, p.Category, p.PreferenceText, p.OwnerId ?? "-"))
            .OrderBy(s => s, StringComparer.Ordinal)],
    });
});

app.Run();

/// <summary>Exposed so the parity script's caller can reference the assembly if it ever needs to.</summary>
public partial class Program;
