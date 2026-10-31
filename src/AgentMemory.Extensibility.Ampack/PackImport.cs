using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// Importing a loaded knowledge pack's records into a host's store, tied to what loading checked (review 1, M9): the pack is
/// still valid when its records are written, and the host's install ledger holds its history, so removing a pack and then
/// importing an older version or other content under a known version is refused, as installing it would be.
/// </summary>
[Experimental("AMEXT001")]
public static class PackImport
{
    /// <summary>
    /// Imports <paramref name="pack"/>'s records through <paramref name="importer"/>. With <paramref name="ledger"/>, the ledger
    /// refuses what it would refuse at install, and records the pack once it is imported.
    /// </summary>
    /// <exception cref="PackLoadException">The pack holds no records to import, is no longer valid, or the ledger refuses it.</exception>
    public static async Task<KnowledgePackImportResult> ImportAsync(this KnowledgePackImporter importer, LoadedPack pack, PackInstallLedger? ledger = null,
        bool allowDowngrade = false, TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Knowledge is not { } knowledge) throw new PackLoadException([$"{pack.Id} is a {pack.Kind} pack: only a knowledge pack's records import this way."]);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (pack.ValidUntil is { } until && now > until)
            throw new PackLoadException([$"{pack.Id} {pack.Version} stopped being valid at {until:u}: load a current version."]);
        if (ledger?.Ledger.Refusal(pack.Id, pack.Version, pack.Digest, allowDowngrade) is { } refusal) throw new PackLoadException([refusal]);
        // Its verified erasures first (review F08, H4: a loaded pack's import never applied them).
        var result = await importer.ImportAsync(knowledge, pack.Erasures, allowDowngrade, cancellationToken).ConfigureAwait(false);
        ledger?.Record(pack);
        return result;
    }
}
