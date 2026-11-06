// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Bundles;

/// <summary>A new signing key: the private key stays with its holder; the public key goes into a host's trust set.</summary>
[Experimental("AMEXT001")]
public sealed record BundleKey(string PrivateKeyPem, string PublicKeyPem, string KeyId);

/// <summary>
/// Signs a bundle (Extensibility 0.9 §A6): a files list naming every file with its length and sha256, and an ECDSA P-256
/// signature over that list's exact bytes. A signature says who made the bundle and that it is unchanged, not that it is safe.
/// </summary>
[Experimental("AMEXT001")]
public static partial class BundleSigning
{
    /// <summary>The files list, at the bundle root.</summary>
    public const string FilesListName = "agentmemory.module.files.json";
    /// <summary>The signature over the files list, at the bundle root.</summary>
    public const string SignatureName = "agentmemory.module.sig";
    /// <summary>The files list format.</summary>
    public const string FilesListFormat = "agentmemory.module.files/v1";
    /// <summary>The only signature algorithm.</summary>
    public const string Algorithm = "ECDSA-P256-SHA256";

    internal const int MaxFiles = 512;
    internal const long MaxBytes = 16L * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates a P-256 key pair.</summary>
    public static BundleKey CreateKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicPem = ecdsa.ExportSubjectPublicKeyInfoPem();
        return new BundleKey(ecdsa.ExportPkcs8PrivateKeyPem(), publicPem, KeyIdOf(ecdsa));
    }

    /// <summary>The first 16 hex characters of the sha256 of the key's SubjectPublicKeyInfo.</summary>
    public static string KeyIdOf(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return KeyIdOf(ecdsa);
    }

    internal static string KeyIdOf(ECDsa key) => Hex(key.ExportSubjectPublicKeyInfo())[..16];

    /// <summary>Lowercase hex sha256. ToHexString().ToLowerInvariant(): this package targets net8.0, which has no ToHexStringLower.</summary>
    internal static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Returns the content plus the two trust files. Paths use <c>/</c> separators and simple segments (letters, digits,
    /// <c>.</c>, <c>_</c>, <c>-</c>, starting with a letter or digit); paths that differ only by case are refused.
    /// </summary>
    /// <param name="content">Every file of the bundle except the trust files.</param>
    /// <param name="id">The module or pack id.</param>
    /// <param name="version">Its version.</param>
    /// <param name="privateKeyPem">The signer's P-256 private key (PKCS#8 or SEC1 PEM).</param>
    /// <param name="entry">For a code module, the path of the assembly that embeds its manifest; null for a knowledge pack.</param>
    public static IReadOnlyDictionary<string, byte[]> Sign(IReadOnlyDictionary<string, byte[]> content, string id, string version,
        string privateKeyPem, string? entry = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        var problems = ContentProblems(content.Keys).ToList();
        if (content.Values.Any(v => v is null)) problems.Add("Every file needs content.");
        if (entry is not null && !content.ContainsKey(entry)) problems.Add($"The entry '{entry}' is not one of the files.");
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems), nameof(content));

        var list = new FilesList
        {
            Format = FilesListFormat, Id = id, Version = version, Entry = entry,
            Files = [.. content.OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new FileEntry { Path = f.Key, Length = f.Value.LongLength, Sha256 = Hex(f.Value) })],
        };
        var listBytes = JsonSerializer.SerializeToUtf8Bytes(list, Json);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        if (ecdsa.KeySize != 256) throw new ArgumentException("The key must be a P-256 key.", nameof(privateKeyPem));
        var signature = new SignatureFile
        {
            KeyId = KeyIdOf(ecdsa), Algorithm = Algorithm,
            Signature = Convert.ToBase64String(ecdsa.SignData(listBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        };
        var signed = new Dictionary<string, byte[]>(content, StringComparer.Ordinal)
        {
            [FilesListName] = listBytes,
            [SignatureName] = JsonSerializer.SerializeToUtf8Bytes(signature, Json),
        };
        return signed.AsReadOnly();
    }

    /// <summary>The problems with a bundle's content paths (the trust files excluded); empty when every path is safe.</summary>
    internal static IEnumerable<string> ContentProblems(IEnumerable<string> paths)
    {
        var all = paths.ToList();
        if (all.Count == 0) yield return "A bundle needs at least one file.";
        if (all.Count > MaxFiles) yield return $"A bundle holds at most {MaxFiles} files.";
        foreach (var path in all)
        {
            if (path is FilesListName or SignatureName) yield return $"'{path}' is reserved for the trust files.";
            else if (!IsSafePath(path)) yield return $"'{path}' is not a safe bundle path.";
        }
        foreach (var clash in all.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Paths differ only by case: {string.Join(", ", clash.Order(StringComparer.Ordinal))}.";
    }

    internal static bool IsSafePath(string path) =>
        path.Length is > 0 and <= 255 && path.Split('/') is { Length: <= 8 } segments && segments.All(s => Segment().IsMatch(s));

    // \z, never $: $ also matches before a final newline (review 6, L4).
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    internal sealed record FilesList
    {
        public string? Format { get; init; }
        public string? Id { get; init; }
        public string? Version { get; init; }
        public string? Entry { get; init; }
        public List<FileEntry>? Files { get; init; }
    }

    internal sealed record FileEntry
    {
        public string? Path { get; init; }
        public long Length { get; init; }
        public string? Sha256 { get; init; }
    }

    internal sealed record SignatureFile
    {
        public string? KeyId { get; init; }
        public string? Algorithm { get; init; }
        public string? Signature { get; init; }
    }
}
