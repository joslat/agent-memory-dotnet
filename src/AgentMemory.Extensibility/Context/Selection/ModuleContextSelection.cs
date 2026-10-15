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

    /// <summary>Selects and renders module items after collection, preserving core ownership in the caller.</summary>
    /// <remarks>Caller cancellation is passed through and checked after awaited work. This method does not
    /// promise a hard deadline or dispose resources belonging to an ignored-cancellation provider.</remarks>
    public async Task<ModuleSelectionResult> SelectAsync(ContextEnvelope envelope, IModuleCandidateRenderer renderer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope); ArgumentNullException.ThrowIfNull(renderer);
        cancellationToken.ThrowIfCancellationRequested();
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
        var (judge, answeredBy) = await JudgeAsync(envelope, entries, cancellationToken).ConfigureAwait(false);
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

    private sealed class Entry(ModuleContextCandidate candidate)
    {
        internal ModuleContextCandidate Candidate { get; } = candidate;
        internal List<ModuleContextCandidate> Origins { get; } = [candidate];
        internal ModuleCandidateDisposition? Disposition { get; set; }
        internal double? Score { get; set; }
        internal bool Admitted { get; set; }
        internal bool OfferedToJudge { get; set; }
    }
}
