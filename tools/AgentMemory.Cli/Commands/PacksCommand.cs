#pragma warning disable AMEXT001 // Explicit operator tooling consumes the held module API.
using System.Text.Json;
using AgentMemory.Extensibility.Bundles;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Signing for bundles (Extensibility 0.13 §K6): <c>keygen</c> creates a developer key pair, <c>sign</c> writes a folder's
/// files list and signature, <c>verify</c> checks a folder as a host would. No store, no model, no network.
/// </summary>
internal sealed class PacksCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public int Execute(CliArgs cli)
    {
        try
        {
            return cli.Subcommand?.ToLowerInvariant() switch
            {
                "keygen" => KeyGen(cli.Get("private"), cli.Get("public")),
                "sign" => Sign(cli.Positionals.ElementAtOrDefault(1), cli.Get("key"), cli.Get("id"), cli.Get("version")),
                "verify" => Verify(cli.Positionals.ElementAtOrDefault(1), cli.Get("developer-key"), cli.Get("pin")),
                _ => Usage("packs needs keygen, sign or verify."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException
            or System.Security.Cryptography.CryptographicException)
        {
            output.WriteLine($"error: packs: {ex.Message}");
            return 1;
        }
    }

    private int KeyGen(string? privatePath, string? publicPath)
    {
        if (privatePath is null || publicPath is null) return Usage("packs keygen needs --private <file> and --public <file>.");
        if (File.Exists(privatePath) || File.Exists(publicPath)) return Usage("packs keygen never overwrites a key file.");
        var key = BundleSigning.CreateKey();
        File.WriteAllText(privatePath, key.PrivateKeyPem);
        File.WriteAllText(publicPath, key.PublicKeyPem);
        output.WriteLine($"packs keygen: key id {key.KeyId}. Keep {privatePath} out of source control; hosts trust {publicPath}.");
        return 0;
    }

    private int Sign(string? folder, string? keyPath, string? id, string? version)
    {
        if (folder is null || keyPath is null) return Usage("packs sign needs <folder> and --key <private key file>.");
        var content = BundleFiles.ReadDirectory(folder)
            .Where(f => f.Key is not (BundleSigning.FilesListName or BundleSigning.SignatureName))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        if ((id is null || version is null) && content.TryGetValue("manifest.json", out var manifest))
        {
            using var document = JsonDocument.Parse(manifest);
            id ??= document.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
            version ??= document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        if (id is null || version is null) return Usage("packs sign needs manifest.json with an id and a version, or --id and --version.");
        var signed = BundleSigning.Sign(content, id, version, File.ReadAllText(keyPath));
        File.WriteAllBytes(Path.Combine(folder, BundleSigning.FilesListName), signed[BundleSigning.FilesListName]);
        File.WriteAllBytes(Path.Combine(folder, BundleSigning.SignatureName), signed[BundleSigning.SignatureName]);
        var trust = new BundleTrust();
        trust.DeveloperKeys.Add(PublicPemOf(File.ReadAllText(keyPath)));
        var bundle = BundleVerifier.Verify(signed, trust).Bundle!;
        output.WriteLine($"packs sign: {id} {version}, {content.Count} files, digest {bundle.Digest}, key id {bundle.KeyId}.");
        return 0;
    }

    private int Verify(string? folder, string? developerKeys, string? pin)
    {
        if (folder is null) return Usage("packs verify needs <folder>.");
        var trust = new BundleTrust();
        foreach (var path in (developerKeys ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            trust.DeveloperKeys.Add(File.ReadAllText(path));
        var result = BundleVerifier.Verify(BundleFiles.ReadDirectory(folder), trust, pin);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            format = "agentmemory.bundle-verification/v1", verified = result.IsVerified,
            id = result.Bundle?.Id, version = result.Bundle?.Version, digest = result.Bundle?.Digest,
            keyId = result.Bundle?.KeyId, signer = result.Bundle?.Signer.ToString().ToLowerInvariant(),
            files = result.Bundle?.Files.Count, problems = result.Problems,
        }, Indented));
        return result.IsVerified ? 0 : 2;
    }

    private static string PublicPemOf(string privatePem)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(privatePem);
        return ecdsa.ExportSubjectPublicKeyInfoPem();
    }

    private int Usage(string message)
    {
        output.WriteLine("error: " + message);
        return 1;
    }
}
