#pragma warning disable AMEXT001 // Erasures map onto the Extensibility preview's store form.
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// Erasures a host holds (feature 08): the ones a pack carries, and ones given on their own, verified under the host's trust
/// policy and mapped to the store's form. One that does not verify erases nothing: no one may withdraw another's records.
/// </summary>
public static class PackErasures
{
    /// <summary>
    /// An erasure that travels on its own (R-CORE-131), verified for <paramref name="packId"/> under the host's trust: null when it
    /// does not verify, so no one may withdraw another's records. Give the result to the importer and to <c>UseErasures</c>.
    /// </summary>
    public static ModuleErasure? Load(byte[] envelope, string packId, PackTrust trust) =>
        Verified(envelope ?? throw new ArgumentNullException(nameof(envelope)), packId ?? throw new ArgumentNullException(nameof(packId)),
            (trust ?? throw new ArgumentNullException(nameof(trust))).Policy, DateTimeOffset.UtcNow);

    /// <summary>The erasures a verified pack carries that verify for it.</summary>
    internal static IReadOnlyList<ModuleErasure> Read(VerifiedPack pack, TrustPolicy policy, DateTimeOffset now) =>
        [.. PackErasure.Carried(pack.Attestations).Select(e => Verified(e.Envelope, pack.Id, policy, now, pack.Manifest.Federation?.Publisher)).OfType<ModuleErasure>()];

    /// <summary>One erasure for a pack, verified; null when it does not verify.</summary>
    internal static ModuleErasure? Verified(byte[] envelope, string packId, TrustPolicy policy, DateTimeOffset now, string? publisherWebId = null)
    {
        var (erasure, _, _) = PackErasure.Verify(envelope, packId, policy, now, publisherWebId);
        return erasure is null ? null : new ModuleErasure
        {
            PackId = erasure.Id, Statement = PackCertificate.IdentityOf(envelope)!, IssuedAt = erasure.IssuedAt, Reason = erasure.Reason, Salt = erasure.Salt,
            Records = [.. erasure.Records.Select(r => new ModuleErasedRecord(r.Type, r.Key, r.KeyHash))],
        };
    }
}
