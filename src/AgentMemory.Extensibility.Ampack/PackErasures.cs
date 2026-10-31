#pragma warning disable AMEXT001 // Erasures map onto the Extensibility preview's store form.
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// Erasures a host holds (feature 08): the ones a pack carries, and ones given on their own, verified under the host's trust
/// policy and mapped to the store's form. One that does not verify erases nothing: no one may withdraw another's records.
/// </summary>
internal static class PackErasures
{
    /// <summary>The erasures a verified pack carries that verify for it.</summary>
    internal static IReadOnlyList<ModuleErasure> Read(VerifiedPack pack, TrustPolicy policy, DateTimeOffset now) =>
        [.. PackErasure.Carried(pack.Attestations).Select(e => Verified(e.Envelope, pack.Id, policy, now)).OfType<ModuleErasure>()];

    /// <summary>One erasure for a pack, verified; null when it does not verify.</summary>
    internal static ModuleErasure? Verified(byte[] envelope, string packId, TrustPolicy policy, DateTimeOffset now)
    {
        var (erasure, _, _) = PackErasure.Verify(envelope, packId, policy, now);
        return erasure is null ? null : new ModuleErasure
        {
            PackId = erasure.Id, Statement = PackCertificate.IdentityOf(envelope)!, IssuedAt = erasure.IssuedAt, Reason = erasure.Reason, Salt = erasure.Salt,
            Records = [.. erasure.Records.Select(r => new ModuleErasedRecord(r.Type, r.Key, r.KeyHash))],
        };
    }
}
