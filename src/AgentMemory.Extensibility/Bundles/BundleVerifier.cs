// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Extensibility.Bundles;

/// <summary>Who signed a verified bundle.</summary>
[Experimental("AMEXT001")]
public enum BundleSigner
{
    /// <summary>The AgentMemory project's key, compiled into this assembly.</summary>
    Official,
    /// <summary>A developer key the host configured.</summary>
    Developer,
}

/// <summary>
/// The host's trust set beyond the official keys: developer keys and revocations. Set from host configuration only, never by
/// a request, an MCP tool or a model. Empty by default, and there is no "allow unsigned" switch.
/// </summary>
/// <remarks>
/// This is the module-bundle path of 1.x, kept for compatibility. It has no key scopes by pack id, no install ledger and no
/// validity dates: a pack meant to travel between organisations is an <c>.ampack</c>, loaded with <c>PackLoader</c> under a
/// trust policy, and its records import with <c>PackImport.ImportAsync</c> (review 1, M9).
/// </remarks>
[Experimental("AMEXT001")]
public sealed class BundleTrust
{
    /// <summary>Developer public keys (SubjectPublicKeyInfo PEM).</summary>
    public IList<string> DeveloperKeys { get; } = new List<string>();
    /// <summary>Withdrawn bundle digests, added to the compiled revocation list.</summary>
    public IList<string> RevokedDigests { get; } = new List<string>();
    /// <summary>Withdrawn key ids, added to the compiled revocation list.</summary>
    public IList<string> RevokedKeyIds { get; } = new List<string>();
}

/// <summary>A bundle whose every file matched its signed files list. The bytes are a private copy, read once.</summary>
[Experimental("AMEXT001")]
public sealed record VerifiedBundle
{
    /// <summary>The module or pack id the files list names.</summary>
    public string Id { get; internal init; } = null!;
    /// <summary>Its version.</summary>
    public string Version { get; internal init; } = null!;
    /// <summary>For a code module, the assembly that embeds its manifest; null for a knowledge pack.</summary>
    public string? Entry { get; internal init; }
    /// <summary>The bundle digest: the sha256 of the files list's exact bytes.</summary>
    public string Digest { get; internal init; } = null!;
    /// <summary>The signing key's id.</summary>
    public string KeyId { get; internal init; } = null!;
    /// <summary>Whether the official key or a developer key signed it.</summary>
    public BundleSigner Signer { get; internal init; }
    /// <summary>The content files by path, without the two trust files.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files { get; internal init; } = null!;
}

/// <summary>The verification result: a bundle, or the reasons it was refused.</summary>
[Experimental("AMEXT001")]
public sealed record BundleVerification
{
    /// <summary>The verified bundle; null when refused.</summary>
    public VerifiedBundle? Bundle { get; init; }
    /// <summary>Why it was refused; empty when verified.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    /// <summary>Whether the bundle verified.</summary>
    public bool IsVerified => Bundle is not null;
}

/// <summary>
/// Verifies a bundle before anything in it is used (Extensibility 0.9 §A6.2): the signing key is trusted and not revoked,
/// the signature holds over the files list's exact bytes, the digest is not revoked and equals a pin when given, and the
/// bundle holds exactly the listed files with their lengths and hashes.
/// </summary>
[Experimental("AMEXT001")]
public static class BundleVerifier
{
    /// <summary>Verifies a bundle read into memory, against the official keys and the host's trust set.</summary>
    /// <param name="files">Every file of the bundle by path, including the two trust files.</param>
    /// <param name="trust">Developer keys and revocations from host configuration.</param>
    /// <param name="pin">An optional expected bundle digest.</param>
    public static BundleVerification Verify(IReadOnlyDictionary<string, byte[]> files, BundleTrust trust, string? pin = null) =>
        Verify(files, trust, pin, OfficialBundleKeys.PublicKeys);

    internal static BundleVerification Verify(IReadOnlyDictionary<string, byte[]> files, BundleTrust trust, string? pin,
        IReadOnlyList<string> officialKeys)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(trust);
        // One private copy, hashed and kept: nothing the caller changes afterwards reaches what was verified.
        var copy = files.ToDictionary(f => f.Key, f => f.Value is null ? null : (byte[])f.Value.Clone(), StringComparer.Ordinal);
        if (!copy.TryGetValue(BundleSigning.FilesListName, out var listBytes) || listBytes is null)
            return Refused($"The bundle has no {BundleSigning.FilesListName}.");
        if (!copy.TryGetValue(BundleSigning.SignatureName, out var signatureBytes) || signatureBytes is null)
            return Refused($"The bundle has no {BundleSigning.SignatureName}.");
        if (copy.Values.Sum(v => v?.LongLength ?? 0) > BundleSigning.MaxBytes) return Refused("The bundle is larger than 16 MiB.");

        BundleSigning.SignatureFile? signature;
        try { signature = JsonSerializer.Deserialize<BundleSigning.SignatureFile>(signatureBytes, BundleSigning.Json); }
        catch (JsonException) { return Refused("The signature file is not valid JSON."); }
        if (signature is not { KeyId: { } keyId, Signature: { } encoded } || signature.Algorithm != BundleSigning.Algorithm)
            return Refused($"The signature file needs a key id, a signature and the algorithm {BundleSigning.Algorithm}.");

        var revokedKeys = OfficialBundleKeys.RevokedKeyIds.Concat(trust.RevokedKeyIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (revokedKeys.Contains(keyId)) return Refused($"The signing key {keyId} is revoked.");
        var (key, signer) = Find(keyId, officialKeys, trust.DeveloperKeys);
        if (key is null) return Refused($"The signing key {keyId} is not trusted by this host.");
        using (key)
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(encoded); }
            catch (FormatException) { return Refused("The signature is not base64."); }
            if (!key.VerifyData(listBytes, raw, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Refused("The signature does not match the files list.");
        }

        var digest = BundleSigning.Hex(listBytes);
        if (OfficialBundleKeys.RevokedDigests.Concat(trust.RevokedDigests).Contains(digest, StringComparer.OrdinalIgnoreCase))
            return Refused($"The bundle {digest} is revoked.");
        if (pin is not null && !string.Equals(pin, digest, StringComparison.OrdinalIgnoreCase))
            return Refused($"The bundle digest {digest} does not equal the pin {pin}.");

        // Parsed only after the signature holds: these are the signer's bytes.
        BundleSigning.FilesList? list;
        try { list = JsonSerializer.Deserialize<BundleSigning.FilesList>(listBytes, BundleSigning.Json); }
        catch (JsonException) { return Refused("The files list is not valid JSON."); }
        var problems = ListProblems(list).ToList();
        if (problems.Count > 0) return new() { Problems = problems };

        var listed = list!.Files!.ToDictionary(f => f.Path!, StringComparer.Ordinal);
        foreach (var (path, bytes) in copy)
        {
            if (path is BundleSigning.FilesListName or BundleSigning.SignatureName) continue;
            if (!listed.TryGetValue(path, out var entry)) problems.Add($"'{path}' is not in the files list.");
            else if (bytes is null || bytes.LongLength != entry.Length || BundleSigning.Hex(bytes) != entry.Sha256)
                problems.Add($"'{path}' does not match its length or sha256 in the files list.");
        }
        foreach (var path in listed.Keys.Where(p => !copy.ContainsKey(p))) problems.Add($"'{path}' is listed but missing.");
        if (problems.Count > 0) return new() { Problems = problems };

        return new()
        {
            Bundle = new VerifiedBundle
            {
                Id = list.Id!, Version = list.Version!, Entry = list.Entry, Digest = digest, KeyId = keyId, Signer = signer,
                Files = copy.Where(f => listed.ContainsKey(f.Key))
                    .ToDictionary(f => f.Key, f => (ReadOnlyMemory<byte>)f.Value!, StringComparer.Ordinal).AsReadOnly(),
            },
        };
    }

    private static IEnumerable<string> ListProblems(BundleSigning.FilesList? list)
    {
        if (list is null) { yield return "The files list is empty."; yield break; }
        if (list.Format != BundleSigning.FilesListFormat) yield return $"The files list format must be {BundleSigning.FilesListFormat}.";
        if (string.IsNullOrWhiteSpace(list.Id) || string.IsNullOrWhiteSpace(list.Version)) yield return "The files list needs an id and a version.";
        if (list.Files is not { } files) { yield return "The files list names no files."; yield break; }
        if (files.Any(f => f.Path is null || f.Sha256 is not { Length: 64 } || !f.Sha256.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)) || f.Length < 0))
        { yield return "Every listed file needs a path, a length and a lowercase sha256."; yield break; }
        foreach (var problem in BundleSigning.ContentProblems(files.Select(f => f.Path!))) yield return problem;
        if (!files.Select(f => f.Path!).SequenceEqual(files.Select(f => f.Path!).Order(StringComparer.Ordinal)))
            yield return "The files list is not in ordinal path order.";
        if (list.Entry is { } entry && files.All(f => f.Path != entry)) yield return $"The entry '{entry}' is not a listed file.";
    }

    private static (ECDsa? Key, BundleSigner Signer) Find(string keyId, IReadOnlyList<string> official, IEnumerable<string> developer)
    {
        foreach (var (pem, signer) in official.Select(p => (p, BundleSigner.Official)).Concat(developer.Select(p => (p, BundleSigner.Developer))))
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
                if (key.KeySize == 256 && BundleSigning.KeyIdOf(key) == keyId) return (key, signer);
            }
            catch (ArgumentException) { }
            catch (CryptographicException) { }
            key.Dispose();
        }
        return (null, default);
    }

    private static BundleVerification Refused(string problem) => new() { Problems = [problem] };
}

/// <summary>
/// The official public keys and the shipped revocation list. Two key slots let a key rotate with one release. Empty until
/// the owner creates the official key (Extensibility 0.13 §K6): until then only configured developer keys verify.
/// </summary>
internal static class OfficialBundleKeys
{
    internal static readonly IReadOnlyList<string> PublicKeys = [];
    internal static readonly IReadOnlyList<string> RevokedKeyIds = [];
    internal static readonly IReadOnlyList<string> RevokedDigests = [];
}
