using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A pack signing key: its id (the SHA-256 of the public key), and both halves as PEM. Keep the private half secret.</summary>
[Experimental("AMEXT001")]
public sealed record PackSigningKey(string KeyId, string PrivateKeyPem, string PublicKeyPem);

/// <summary>
/// Authoring memory packs from a host or a tool such as DemoBrain's composer (pack format v1.2): migrate a module bundle to
/// the v1.2 layout, create keys, write a trust policy, and build, sign and pack a folder's files into an <c>.ampack</c>.
/// The same operations as the <c>ampack</c> command line.
/// </summary>
[Experimental("AMEXT001")]
public static class PackAuthoring
{
    /// <summary>A new ECDSA P-256 signing key.</summary>
    public static PackSigningKey CreateKey()
    {
        var key = PackKey.Create();
        return new PackSigningKey(key.KeyId, key.PrivateKeyPem, key.PublicKeyPem);
    }

    /// <summary>An existing module bundle (<c>manifest.json</c>, seed files, icon) as v1.2 pack files (PACK-migrate).</summary>
    public static IReadOnlyDictionary<string, byte[]> FromModuleBundle(IReadOnlyDictionary<string, byte[]> bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return PackLayout.FromModule(bundle);
    }

    /// <summary>
    /// Builds a pack from its source files: writes the canonical file list, signs it with <paramref name="privateKeyPem"/> in
    /// <paramref name="role"/>, and returns the <c>.ampack</c> bytes.
    /// </summary>
    /// <exception cref="PackLoadException">The files do not form a valid pack.</exception>
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> source, string privateKeyPem, string role = "publisher")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        Dictionary<string, byte[]> files;
        try { (files, _) = PackBuilder.Build(source); }
        catch (InvalidDataException e) { throw new PackLoadException([e.Message]); }
        if (PackVerifier.CheckIntegrity(new PackContent(files, "build")).Problems is { Count: > 0 } problems) throw new PackLoadException(problems);
        PackBuilder.Sign(files, privateKeyPem, role, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return PackBuilder.Pack(files);
    }

    /// <summary>A trust policy (<c>agentmemory.pack.trust/1.1</c>) that trusts one key for the given namespaces as publisher.</summary>
    public static string TrustPolicy(string publicKeyPem, params string[] idPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        if (idPrefixes is not { Length: > 0 }) throw new ArgumentException("A key is trusted for at least one namespace.", nameof(idPrefixes));
        var policy = new JsonObject
        {
            ["format"] = global::AgentMemory.Packs.Format.TrustPolicy.Format,
            ["keys"] = new JsonArray((JsonNode)new JsonObject
            {
                ["keyId"] = PackKey.KeyIdOf(publicKeyPem), ["publicKey"] = publicKeyPem,
                ["idPrefixes"] = new JsonArray([.. idPrefixes.Select(p => (JsonNode)p)]), ["roles"] = new JsonArray((JsonNode)"publisher"),
            }),
        };
        return Encoding.UTF8.GetString(PackLayout.Write(policy));
    }
}
