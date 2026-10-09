using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Decides, for a turn, what long-term memory to write: the store-aware alternative to the extractors.
/// </summary>
/// <remarks>
/// <para>
/// The extractors read a turn and propose every fact, preference, person and connection it mentions, without seeing what is
/// already stored. Measured on a 12-session world, each turn writing into the store the earlier turns made, that path wrote
/// 629 rows where 105 were asked for, kept 45 duplicates, wrote something on half of the turns that should store nothing,
/// and closed none of the 31 memories the person replaced (storage accuracy 42%). A writer shown the owner's most relevant
/// stored memories that proposes only what is new (add), what changes a stored memory (replace, naming it) or what was
/// wrong (correct, naming it), quoting the words it rests on, or nothing, reached 90.8% on the same world and setup in the
/// research harness, with precision 96.8% and no wrong closure.
/// </para>
/// <para>
/// Used only when registered and <see cref="IsEnabled"/>: the extraction stage then asks the writer instead of the
/// extractors, for a window with exactly one user message (a turn, as it was measured); any other window goes to the
/// extractors as configured. A memory the writer says replaces or corrects a stored one carries that memory's id
/// (<see cref="ExtractedFact.ReplacesId"/>, <see cref="ExtractedPreference.ReplacesId"/>); the stored memory is closed only
/// when a registered <see cref="IMemoryUpdateJudge"/> confirms the pair. Without a judge nothing is closed: a stale memory
/// is a smaller harm than a true one erased.
/// </para>
/// </remarks>
[Experimental("AMWRITE001")]
public interface IMemoryWriter
{
    /// <summary>Whether the extraction stage should ask this writer instead of the extractors.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// What the turn's <see cref="ExtractionWindow.Targets"/> tell that is worth keeping, in the light of the owner's stored
    /// memories; the <see cref="ExtractionWindow.Context"/> turns are read only to understand them.
    /// </summary>
    Task<UnifiedExtractionResult> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a <see cref="IMemoryWriter"/> writes about.</summary>
/// <param name="Window">The turns to write from, and the earlier turns that explain them.</param>
/// <param name="Scope">The scope of the write. The writer reads only the write owner's own memories (none of another owner's,
/// none shared), and with no owner only the ownerless ones; it can close only the owner's own.</param>
[Experimental("AMWRITE001")]
public sealed record MemoryWriteRequest(ExtractionWindow Window, MemoryScope? Scope);

/// <summary>
/// An update judge asked only when the registered <see cref="IMemoryUpdateJudge"/> is missing, switched off, or fails (an
/// outside judge that is down, or left out after repeated failures), and only for the closings a store-aware writer names:
/// the write path's second tier. Measured (strategy PLAN 41.26 (c), world 6, three store runs each): the host's chat model
/// asked the gate's question confirmed the writer's closings as JEV did (stale values 0.33 a store against 0.33, complete
/// recall 76.4% against 75.0%, precision 97.7% against 97.9%); with no judge at all, 16 replaced values a store stayed live.
/// </summary>
[Experimental("AMWRITE001")]
public interface IMemoryUpdateJudgeFallback : IMemoryUpdateJudge
{
}
