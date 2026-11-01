using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;

namespace AgentMemory.Extensibility.Modules;

/// <summary>One record a pack would have offered on a live turn while it ran dark (feature 11, R2): never its text.</summary>
[Experimental("AMEXT001")]
public sealed record ShadowOffer
{
    /// <summary>The record version's id.</summary>
    public required string RecordId { get; init; }
    /// <summary>Its key.</summary>
    public required string Key { get; init; }
    /// <summary>Its type.</summary>
    public required string Type { get; init; }
    /// <summary>The first relation of the walk's reason path that reached it; null for a view's offer or an anchor's own record.</summary>
    public string? ViaRelation { get; init; }
    /// <summary>What the host's labeller matches it by (for a code map's test, its file), as the host's locator gave it; null when none.</summary>
    public string? Locator { get; init; }
}

/// <summary>A live turn a pack ran dark on (feature 11, R2): when, from what anchors, and what it would have offered.</summary>
[Experimental("AMEXT001")]
public sealed record ShadowTurn
{
    /// <summary>The module that ran dark.</summary>
    public required string ModuleId { get; init; }
    /// <summary>The turn, the same for every contributor of the module on it, so a turn counts once.</summary>
    public string TurnId { get; init; } = "";
    /// <summary>The digest of the pack that ran dark, so a decision binds to exactly it; null for a module compiled into the host.</summary>
    public string? PackDigest { get; init; }
    /// <summary>Its version.</summary>
    public string? Version { get; init; }
    /// <summary>How long its contributor took on the turn, in milliseconds.</summary>
    public double Milliseconds { get; init; }
    /// <summary>When the turn was assembled: the time its outcome is looked for after.</summary>
    public required DateTimeOffset At { get; init; }
    /// <summary>The turn's session.</summary>
    public string? SessionId { get; init; }
    /// <summary>The turn's anchors, as <c>kind:value</c>.</summary>
    public IReadOnlyList<string> Anchors { get; init; } = [];
    /// <summary>What it would have offered.</summary>
    public IReadOnlyList<ShadowOffer> Offers { get; init; } = [];
}

/// <summary>Where a host keeps its shadow turns (feature 11, R2). Shadow logs never leave the host (R5).</summary>
[Experimental("AMEXT001")]
public interface IShadowRecorder
{
    /// <summary>Records one turn.</summary>
    ValueTask RecordAsync(ShadowTurn turn, CancellationToken cancellationToken = default);
}

/// <summary>A shadow log in one local file, a JSON line per turn (feature 11, R2).</summary>
[Experimental("AMEXT001")]
public sealed class ShadowLogFile(string path) : IShadowRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public async ValueTask RecordAsync(ShadowTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var line = JsonSerializer.Serialize(turn, Json) + "\n";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await File.AppendAllTextAsync(path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Every turn in a shadow log, oldest first; a line that is not a turn is refused with its number.</summary>
    /// <exception cref="InvalidDataException">A line is not a shadow turn.</exception>
    public static IReadOnlyList<ShadowTurn> Read(string path)
    {
        var turns = new List<ShadowTurn>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Length == 0) continue;
            try { turns.Add(JsonSerializer.Deserialize<ShadowTurn>(line, Json) ?? throw new JsonException("null")); }
            catch (JsonException e) { throw new InvalidDataException($"{path}:{number}: not a shadow turn ({e.Message})."); }
        }
        return [.. turns.OrderBy(t => t.At)];
    }
}

// A module running dark (feature 11, R2): its contributors run on every turn, and what they would offer goes to the recorder,
// never into the context.
[Experimental("AMEXT001")]
internal sealed class ShadowContributor(IContextContributor inner, string module, IShadowRecorder recorder, Func<ContextItem, string?>? locate, TimeProvider clock,
    string? packDigest = null, string? version = null)
    : IContextContributor
{
    // One id per turn, whatever contributors the module has on it.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContextRequest, string> Turns = new();

    public ContextContributorDescriptor Descriptor => inner.Descriptor;

    public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken) => inner.AppliesAsync(request, cancellationToken);

    public async Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var section = await inner.ContributeAsync(request, cancellationToken).ConfigureAwait(false);
        await RecordAsync(request, section, watch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await inner.ContributeResultAsync(request, cancellationToken).ConfigureAwait(false);
        await RecordAsync(request, result.Section, watch.Elapsed.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        return new ContextContributionResult
        {
            OmissionReason = ContextOmissionReason.Denied,
            Detail = $"{module} runs dark (a shadow trial): what it would offer is recorded on this host, not used.",
        };
    }

    private async Task RecordAsync(ContextRequest request, ContextSection? section, double milliseconds, CancellationToken cancellationToken)
    {
        var offers = (section?.Items ?? []).Where(i => i.Provenance is not null).Select(i => new ShadowOffer
        {
            RecordId = i.Provenance!.RecordVersionId, Key = i.Provenance.RecordKey, Type = i.Provenance.RecordType,
            ViaRelation = i.Provenance.ViaRelation, Locator = locate?.Invoke(i),
        }).ToList();
        // Every turn, offered or not (review 11-13, H3: a turn it offered nothing on was not logged, so its hit rate rose).
        await recorder.RecordAsync(new ShadowTurn
        {
            ModuleId = module, TurnId = Turns.GetValue(request, _ => Guid.NewGuid().ToString("N")), PackDigest = packDigest, Version = version,
            Milliseconds = Math.Round(milliseconds, 1), At = request.AsOf ?? clock.GetUtcNow(), SessionId = request.SessionId,
            Anchors = [.. request.Anchors.Select(a => a.Kind.ToString().ToLowerInvariant() + ":" + a.Value)], Offers = offers,
        }, cancellationToken).ConfigureAwait(false);
    }
}
