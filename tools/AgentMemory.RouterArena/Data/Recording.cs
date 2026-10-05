using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.RouterArena.Data;

/// <summary>
/// A recording (<c>routing-recording/1</c>): what recall returned for every matrix turn, kind by kind, with each item's
/// score, rank and owner. Written once by <c>record</c>; every fight reads it. Three recalls per turn: as the library ships
/// (<see cref="TurnRecord.Today"/>), wide open (<see cref="TurnRecord.Wide"/>: every kind up to 50, no floor) and, for a
/// follow-up, wide open on the turn rewritten with the one before (<see cref="TurnRecord.WithPrior"/>).
/// </summary>
public sealed record Recording(
    string Format, Source World, Source Items, string Embeddings, DateTimeOffset AskedAt, string Owner, string Session,
    Caps Today, IReadOnlyList<TurnRecord> Records)
{
    /// <summary>Whether the shipped options split compound questions (the conversational preset does).</summary>
    public bool FanOutShipped { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The file's sha256 is not part of the record; <see cref="ReadFile"/> returns it beside.</summary>
    public static (Recording Recording, string Sha256) ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var recording = JsonSerializer.Deserialize<Recording>(bytes, Json) ?? throw new JsonException("the recording is empty");
        return (recording, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}

public sealed record Source(string Path, string Sha256);

/// <summary>The recall options the library ships with, so a wide recall can be cut the way it would be today.</summary>
public sealed record Caps(int Facts, int Entities, int Relationships, int Preferences, int RelevantMessages, int RecentMessages, double MinSimilarity)
{
    /// <summary>The traces cap (recordings before the doors had none: the shipped default).</summary>
    public int Traces { get; init; } = 3;
}

/// <param name="Variants">The shipped recall at other settings ("0.65x2": floor 0.65, caps doubled), recorded for real.</param>
/// <param name="TodayNoFanOut">The shipped recall with fan-out off: the old method without the adaptive router.</param>
/// <param name="FanOut">The adaptive router's witness on the shipped recall: did its gate fire, which rule, which legs.</param>
/// <param name="Doors">
/// 40.72: the turn recalled through the doors that are ways of reading the store rather than sections of it: "temporal"
/// (valid time current; "temporal:wide" the same wide open), "prospective" (valid time current with prospective firing:
/// the DUE and EXPIRING facts), "asOf:yyyy-MM-dd" (bi-temporal: what was true then), "forgetting" (legible forgetting
/// after the decay pass), "faded" (the faded memories the turn matches, asked directly).
/// </param>
public sealed record TurnRecord(
    string Id, string Text, Section Today, Section Wide, Section? WithPrior,
    IReadOnlyDictionary<string, Section>? Variants = null, Section? TodayNoFanOut = null, FanOutWitness? FanOut = null,
    IReadOnlyDictionary<string, Section>? Doors = null);

/// <summary>One leg of a split question: its type, its text, what it found, and what it added that the whole question did not.</summary>
public sealed record FanOutLeg(string Affinity, string Text, int Retrieved, int Unique, int Survived);

/// <summary>The fan-out report of one recall: null when the planner never ran.</summary>
public sealed record FanOutWitness(bool GateFired, IReadOnlyList<string> Rules, string? Deriver, IReadOnlyList<FanOutLeg> Legs, string? VoidReason)
{
    public static FanOutWitness? Of(RecallFanOutReport? report) => report is null ? null : new(
        report.GateFired, report.FiredRules, report.DeriverId,
        [.. report.SubQueries.Select(q => new FanOutLeg(q.Affinity.ToString(), q.QueryText, q.ItemsRetrieved, q.UniqueContributions, q.SurvivedBudget))],
        report.VoidReason);
}

/// <summary>
/// One recalled item: its key in the world's terms, its score when the provider gave one, its rank, its owner (null for
/// shared), and its kind when it has one ("derived" for a computed fact; "episode" or "procedure" for a trace).
/// </summary>
public sealed record Hit(string Key, double? Score, int Rank, string? Owner = null, string? Kind = null);

/// <summary>What one recall returned, section by section.</summary>
public sealed record Section(
    IReadOnlyList<Hit> Facts, IReadOnlyList<Hit> Entities, IReadOnlyList<Hit> Relationships, IReadOnlyList<Hit> Preferences,
    IReadOnlyList<Hit> RelevantMessages, IReadOnlyList<Hit> RecentMessages, string? WorkingMemory)
{
    /// <summary>Reasoning traces recalled by task similarity: episodes and procedures (the reasoning and procedural doors).</summary>
    public IReadOnlyList<Hit> Traces { get; init; } = [];

    /// <summary>The DUE facts prospective firing surfaced (the prospective door).</summary>
    public IReadOnlyList<Hit> Due { get; init; } = [];

    /// <summary>The EXPIRING facts prospective firing surfaced.</summary>
    public IReadOnlyList<Hit> Expiring { get; init; } = [];

    /// <summary>What legible forgetting said had faded: topic, count, when it aged out (the forgetting switch).</summary>
    public IReadOnlyList<string> Forgotten { get; init; } = [];

    public static Section Of(MemoryContext context) => new(
        Hits(context.RelevantFacts, f => f.FactId, f => $"{f.Subject} | {f.Predicate} | {f.Object}", f => f.OwnerId, FactKind),
        Hits(context.RelevantEntities, e => e.EntityId, e => e.Name, e => e.OwnerId),
        [.. context.RelevantRelationships.Items.Select((r, i) =>
            new Hit($"{r.SourceName} -[{r.Relationship.RelationshipType}]-> {r.TargetName}", null, i + 1))],
        Hits(context.RelevantPreferences, p => p.PreferenceId, p => p.PreferenceText, p => p.OwnerId),
        Hits(context.RelevantMessages, m => m.MessageId, m => m.Content, _ => null),
        [.. context.RecentMessages.Items.Select((m, i) => new Hit(m.Content, null, i + 1))],
        context.WorkingMemoryBlock)
    {
        Traces = Hits(context.SimilarTraces, t => t.TraceId, t => t.Task, t => t.OwnerId,
            t => t.Kind == TraceKind.Procedure ? "procedure" : "episode"),
        Due = Hits(context.DueFacts, f => f.FactId, f => $"{f.Subject} | {f.Predicate} | {f.Object}", f => f.OwnerId, FactKind),
        Expiring = Hits(context.ExpiringFacts, f => f.FactId, f => $"{f.Subject} | {f.Predicate} | {f.Object}", f => f.OwnerId, FactKind),
        Forgotten = [.. context.ForgottenTopics.Select(f => $"{f.Topic} ({f.Count}, aged out {f.AgedOutUtc:yyyy-MM-dd})")],
    };

    private static string? FactKind(Fact fact) => fact.Metadata.IsDerived() ? "derived" : null;

    private static IReadOnlyList<Hit> Hits<T>(MemoryContextSection<T> section, Func<T, string> id, Func<T, string> key, Func<T, string?> owner,
        Func<T, string?>? kind = null)
    {
        var scores = section.RankedItems.ToDictionary(r => r.ItemId, r => r.Score, StringComparer.Ordinal);
        return [.. section.Items.Select((item, i) =>
            new Hit(key(item), scores.TryGetValue(id(item), out var s) ? s : null, i + 1, owner(item), kind?.Invoke(item)))];
    }
}
