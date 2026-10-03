using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// G3 (PLAN 40.47): an owner's data as a whole: erase it, export it, import it under an owner. Shared knowledge (no owner)
/// and every other owner are never touched.
/// </summary>
public interface IMemoryOwnerDataService
{
    /// <summary>
    /// Deletes everything of <paramref name="ownerId"/>'s: every node stamped with the owner (memories, traces, the profile,
    /// recall audits) and their conversations with every message in them. Irreversible; the report says what went.
    /// </summary>
    Task<OwnerErasure> EraseAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>The owner's long-term memory, live and closed, with its links.</summary>
    Task<OwnerMemoryExport> ExportAsync(string ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="export"/> under <paramref name="ownerId"/> with fresh ids, embedded again, closings and links
    /// kept. Importing into an owner who already has memories adds to them.
    /// </summary>
    Task<OwnerImportResult> ImportAsync(OwnerMemoryExport export, string ownerId, CancellationToken cancellationToken = default);
}
