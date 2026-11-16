// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>Explicit, pure updates to evidence carried beside immutable signed pack content. No IO or default registration.</summary>
[Experimental("AMEXT001")]
public static class PackArchiveUpdates
{
    /// <summary>Merges carried attestations from two wholly verified copies of identical content, retaining earlier notices.
    /// Conflicting evidence paths or different content are refused; signed content and its digest never change.</summary>
    public static byte[] MergeAttestations(byte[] existing, byte[] incoming, PackTrust trust)
    {
        ArgumentNullException.ThrowIfNull(existing); ArgumentNullException.ThrowIfNull(incoming); ArgumentNullException.ThrowIfNull(trust);
        try
        {
            var before = PackReader.ReadArchive(existing, "existing"); var after = PackReader.ReadArchive(incoming, "incoming");
            var now = DateTimeOffset.UtcNow;
            var verifiedBefore = PackVerifier.VerifyWhole(before, trust.Policy, now);
            var verifiedAfter = PackVerifier.VerifyWhole(after, trust.Policy, now);
            if (!verifiedBefore.IsVerified) throw new PackLoadException(verifiedBefore.Problems);
            if (!verifiedAfter.IsVerified) throw new PackLoadException(verifiedAfter.Problems);
            if (verifiedBefore.Pack!.Digest != verifiedAfter.Pack!.Digest)
                throw new PackLoadException(["Attestations can be merged only for identical signed content."]);
            var files = before.Files.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var (path, bytes) in after.Files.Where(p => p.Key.StartsWith("META/attestations/", StringComparison.Ordinal)))
            {
                if (files.TryGetValue(path, out var held) && !held.AsSpan().SequenceEqual(bytes))
                    throw new PackLoadException(["Carried attestations conflict at " + path]);
                files[path] = bytes;
            }
            var merged = PackBuilder.Pack(files);
            var verified = PackVerifier.VerifyWhole(PackReader.ReadArchive(merged, "merged"), trust.Policy, now);
            if (!verified.IsVerified) throw new PackLoadException(verified.Problems);
            return merged;
        }
        catch (InvalidDataException error) { throw new PackLoadException([error.Message]); }
    }
}
