using AgentMemory.Extensibility.Ampack;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Ampack;

/// <summary>
/// <c>ampack export mem &lt;pack&gt;</c> (feature 15, R5): a verified knowledge pack written as a <c>.mem</c> bundle folder,
/// its manifest signed with Ed25519 when <c>--mem-key</c> names a PKCS#8 PEM key (as <c>openssl genpkey -algorithm ed25519</c>
/// writes one).
/// </summary>
internal static partial class Program
{
    // ampack export mem <pack.ampack> --policy <trust.json> --out <folder> [--mem-key <ed25519.pem>] [--at <instant>]
    private static int Export(Options o)
    {
        var kind = o.Positional(0, "export kind (mem)");
        if (kind != "mem") throw new UsageException($"Unknown export kind '{kind}': mem.");
        var pack = PackLoader.Load(o.Positional(1, "pack.ampack"), Trust(o));
        var output = o.Required("out");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new UsageException($"'{output}' is not empty; a bundle is written into a new folder.");
        var at = o.Value("at") is { } given
            ? DateTimeOffset.TryParseExact(given, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                ? t : throw new UsageException("--at is an instant in UTC to the second, as 2026-11-01T12:00:00Z.")
            : DateTimeOffset.UtcNow;
        Func<byte[], (byte[], byte[])>? sign = null;
        if (o.Value("mem-key") is { } keyFile)
        {
            var pem = Text(keyFile, "The .mem signing key");
            var body = string.Concat(pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
            if (!pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal)
                || PrivateKeyFactory.CreateKey(Convert.FromBase64String(body)) is not Ed25519PrivateKeyParameters key)
                throw new UsageException("--mem-key is an Ed25519 private key in PKCS#8 PEM (openssl genpkey -algorithm ed25519).");
            sign = message =>
            {
                var signer = new Ed25519Signer();
                signer.Init(true, key);
                signer.BlockUpdate(message, 0, message.Length);
                return (key.GeneratePublicKey().GetEncoded(), signer.GenerateSignature());
            };
        }
        MemExport export;
        try { export = PackExports.ToMem(pack, at, "agentmemory-ampack/1", sign); }
        catch (InvalidOperationException e) { throw new UsageException(e.Message); }
        foreach (var (path, bytes) in export.Files)
        {
            var target = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        Console.WriteLine($"Exported {export.Episodes} episode(s) and {export.Tombstones} tombstone(s) from {pack.Id} {pack.Version} as a .mem bundle{(sign is null ? "" : ", signed")}.");
        Console.WriteLine($"  wrote   {output}");
        foreach (var note in export.Notes) Console.WriteLine("  " + Shown(note, 300));
        return 0;
    }
}
