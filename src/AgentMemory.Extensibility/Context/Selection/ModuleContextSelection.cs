using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Extensibility.Context;

/// <summary>Explicit module selection, independent of frameworks and gate implementations.</summary>
[Experimental("AMEXT001")]
public sealed partial class ModuleContextSelection
{
    private readonly ModuleSelectionOptions _options;
    private readonly Func<IMemoryGate?>? _gateFactory;

    /// <summary>Creates an explicit selection path. Supplying a factory alone does not enable judging.</summary>
    public ModuleContextSelection(ModuleSelectionOptions options, Func<IMemoryGate?>? gateFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.Threshold) || options.Threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Threshold must be finite and in [0,1].");
        foreach (var limit in new[] { options.MaxCandidates, options.MaxItemsPerContributor, options.MaxItems,
                     options.MaxCandidateUtf8Bytes, options.MaxJudgeUtf8Bytes, options.MaxRenderedUtf8Bytes, options.MaxConversationMessages })
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(options), "Limits must be non-negative.");
        ArgumentNullException.ThrowIfNull(options.ContributorItemLimits);
        if (options.ContributorItemLimits.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value < 0))
            throw new ArgumentException("Contributor limits require an identity and a non-negative value.", nameof(options));
        _options = options with { ContributorItemLimits = options.ContributorItemLimits.ToFrozenDictionary(StringComparer.Ordinal) };
        _gateFactory = gateFactory;
    }

    /// <summary>Selects and renders module items after collection, judging them in a gate call of their own.</summary>
    /// <remarks>Caller cancellation is passed through and checked after awaited work. This method does not
    /// promise a hard deadline or dispose resources belonging to an ignored-cancellation provider.
    /// A host whose core recall has a judging gate uses <see cref="PrepareForCorePool"/> instead, so module and core
    /// memories are judged in one call (Extensibility 0.12, R1).</remarks>
    public async Task<ModuleSelectionResult> SelectAsync(ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
        var (entries, contributors) = Prepare(envelope, renderer, cancellationToken);
        var (judge, answeredBy) = await JudgeAsync(envelope, entries, cancellationToken).ConfigureAwait(false);
        return Render(envelope, renderer, entries, contributors, judge, answeredBy, cancellationToken);
    }

    /// <summary>
    /// Collects, bounds and admits module items, and returns the candidates for the core gate to judge with core memories
    /// (empty when judging is off). Finish with <see cref="ModuleSelectionTurn.Complete"/> once core recall has decided.
    /// </summary>
    /// <remarks>The same limits, admission, duplicate handling and receipts as <see cref="SelectAsync"/>; only who
    /// judges changes. Nothing here resolves or calls a gate.</remarks>
    public ModuleSelectionTurn PrepareForCorePool(ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
        var (entries, contributors) = Prepare(envelope, renderer, cancellationToken);
        var offered = _options.UseJudge ? Offer(entries) : [];
        return new ModuleSelectionTurn(this, envelope, renderer, entries, contributors, offered);
    }

    /// <summary>Collection, then the size and admission checks every path shares.</summary>
    private (List<Entry> Entries, List<ModuleContributorSelection> Contributors) Prepare(ContextEnvelope envelope,
        IModuleCandidateRenderer renderer, CancellationToken cancellationToken)
    {
        var (entries, contributors) = Collect(envelope, cancellationToken);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Disposition is not null) continue;
            if (_options.MaxItems == 0)
            { entry.Disposition = ModuleCandidateDisposition.ItemLimit; continue; }
            if (_options.MaxRenderedUtf8Bytes == 0)
            { entry.Disposition = ModuleCandidateDisposition.OutputByteLimit; continue; }
            if (Encoding.UTF8.GetByteCount(entry.Candidate.Text) > _options.MaxCandidateUtf8Bytes)
            { entry.Disposition = ModuleCandidateDisposition.CandidateByteLimit; continue; }
            try
            {
                entry.Admitted = renderer.Admit(entry.Candidate);
                if (!entry.Admitted) entry.Disposition = ModuleCandidateDisposition.Denied;
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { entry.Disposition = ModuleCandidateDisposition.Unavailable; }
        }
        return (entries, contributors);
    }

    /// <summary>Renders the eligible entries within the item and byte allowances, in score order when a decision applied.</summary>
    internal ModuleSelectionResult Render(ContextEnvelope envelope, IModuleCandidateRenderer renderer, List<Entry> entries,
        List<ModuleContributorSelection> contributors, ModuleJudgeOutcome judge, string? answeredBy, CancellationToken cancellationToken)
    {
        var eligible = entries.Where(e => e.Disposition is null);
        if (judge == ModuleJudgeOutcome.Applied) eligible = eligible.OrderByDescending(e => e.Score);
        var kept = new List<ModuleContextCandidate>();
        var rendered = string.Empty; var bytes = 0;
        foreach (var entry in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (kept.Count >= _options.MaxItems) { entry.Disposition = ModuleCandidateDisposition.ItemLimit; continue; }
            try
            {
                var trial = kept.Append(entry.Candidate).ToArray();
                var text = renderer.Render(Array.AsReadOnly(trial));
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("A nonempty selection requires a rendered block.");
                var size = Encoding.UTF8.GetByteCount(text);
                if (size > _options.MaxRenderedUtf8Bytes) { entry.Disposition = ModuleCandidateDisposition.OutputByteLimit; continue; }
                kept.Add(entry.Candidate); rendered = text; bytes = size; entry.Disposition = ModuleCandidateDisposition.Kept;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { entry.Disposition = ModuleCandidateDisposition.Unavailable; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new()
        {
            RenderedText = rendered, RenderedUtf8Bytes = bytes, JudgeOutcome = judge, AnsweredBy = answeredBy,
            CompilationOmissions = Array.AsReadOnly(envelope.Omissions.ToArray()), Contributors = contributors.AsReadOnly(),
            Decisions = entries.Select(e => new ModuleCandidateDecision
            {
                Candidate = e.Candidate, Origins = e.Origins.AsReadOnly(), Disposition = e.Disposition!.Value, Score = e.Score,
                Admitted = e.Admitted, OfferedToJudge = e.OfferedToJudge
            }).ToList().AsReadOnly()
        };
    }

    internal sealed class Entry(ModuleContextCandidate candidate)
    {
        internal ModuleContextCandidate Candidate { get; } = candidate;
        internal List<ModuleContextCandidate> Origins { get; } = [candidate];
        internal ModuleCandidateDisposition? Disposition { get; set; }
        internal double? Score { get; set; }
        internal bool Admitted { get; set; }
        internal bool OfferedToJudge { get; set; }
    }
}

/// <summary>
/// A module selection whose candidates the core gate judges with core memories (Extensibility 0.12, R1): prepared by
/// <see cref="ModuleContextSelection.PrepareForCorePool"/>, finished by <see cref="Complete"/> with the core gate's decisions.
/// </summary>
[Experimental("AMEXT001")]
public sealed class ModuleSelectionTurn
{
    private readonly ModuleContextSelection _selection;
    private readonly ContextEnvelope _envelope;
    private readonly IModuleCandidateRenderer _renderer;
    private readonly List<ModuleContextSelection.Entry> _entries;
    private readonly List<ModuleContributorSelection> _contributors;
    private readonly List<ModuleContextSelection.Entry> _offered;
    private int _completed;

    internal ModuleSelectionTurn(ModuleContextSelection selection, ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        List<ModuleContextSelection.Entry> entries, List<ModuleContributorSelection> contributors, List<ModuleContextSelection.Entry> offered)
    {
        _selection = selection; _envelope = envelope; _renderer = renderer;
        _entries = entries; _contributors = contributors; _offered = offered;
        Companions = offered.Select(e => new MemoryGateCandidate(e.Candidate.Key, e.Candidate.Binding.ModuleId, e.Candidate.Text))
            .ToList().AsReadOnly();
    }

    /// <summary>The admitted, bounded candidates for the core gate: module/contributor/record keys, the module id as type.</summary>
    public IReadOnlyList<MemoryGateCandidate> Companions { get; }

    /// <summary>
    /// Applies the core gate's decisions and renders, once. Null decisions (no judging gate, the floor, a failure or a late
    /// module search) keep the admitted, bounded candidates and report <see cref="ModuleJudgeOutcome.Unavailable"/>.
    /// </summary>
    /// <param name="decisions">The core gate's probabilities for <see cref="Companions"/>, or null.</param>
    /// <param name="answeredBy">Which judges answered, for the receipt.</param>
    /// <param name="offeredToJudge">Whether a gate call carried the companions, even one that failed; receipts say so.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public ModuleSelectionResult Complete(IReadOnlyDictionary<string, double>? decisions, string? answeredBy = null,
        bool offeredToJudge = false, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) throw new InvalidOperationException("A selection turn completes once.");
        cancellationToken.ThrowIfCancellationRequested();
        var (outcome, by) = _selection.ApplyCoreDecisions(_offered, Companions.Count, decisions, answeredBy, offeredToJudge);
        return _selection.Render(_envelope, _renderer, _entries, _contributors, outcome, by, cancellationToken);
    }
}
