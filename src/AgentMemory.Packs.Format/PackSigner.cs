// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;

namespace AgentMemory.Packs.Format;

/// <summary>
/// What signs a pack or a revocation document (PACK-toolset): a P-256 private key held in memory, or a command that signs with
/// a key it never shows, so a key can stay in an OS keychain, a cloud KMS or an HSM. Either way the signature is checked
/// against the public key before anything is written.
/// </summary>
internal abstract class PackSigner : IDisposable
{
    /// <summary>The signing key's public half, PEM.</summary>
    internal abstract string PublicKeyPem { get; }

    /// <summary>The key id: the SHA-256 of the SubjectPublicKeyInfo.</summary>
    internal string KeyId => PackKey.KeyIdOf(PublicKeyPem);

    /// <summary>An ECDSA P-256 SHA-256 signature over <paramref name="data"/>, DER encoded, checked against the public key.</summary>
    internal byte[] Sign(byte[] data)
    {
        // Review 1, L9: whatever the signer printed, the stored signature is minimal DER, as both verifiers require.
        var signature = StrictDer(SignData(data));
        if (!Dsse.IsStrictDer(signature)) throw new InvalidDataException("The signer returned a signature no verifier accepts as strict DER.");
        using var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        if (!IsP256(key)) throw new InvalidDataException("The public key is not a P-256 key.");
        if (!key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("The signature does not verify with the public key: the signer used another key, or another algorithm.");
        return signature;
    }

    // P-256 by its curve's OID, not its size: secp256k1 and brainpoolP256 are 256-bit too, and no verifier accepts them.
    // Platforms name it differently (CNG ECDSA_P256 or nistP256, OpenSSL prime256v1); the OID is the same everywhere.
    internal static bool IsP256(ECDsa key) =>
        key.ExportParameters(false).Curve.Oid is { } oid
        && (oid.Value == "1.2.840.10045.3.1.7" || oid.FriendlyName is "nistP256" or "ECDSA_P256" or "prime256v1" or "secp256r1");

    // A DER (or BER) ECDSA signature re-encoded as minimal DER: SEQUENCE { INTEGER r, INTEGER s }.
    private static byte[] StrictDer(byte[] signature)
    {
        try
        {
            var reader = new AsnReader(signature, AsnEncodingRules.BER);
            var sequence = reader.ReadSequence();
            // Each integer read as unsigned (review 2: a signer that drops the sign pad writes r or s as a negative integer).
            var r = new BigInteger(sequence.ReadIntegerBytes().Span, isUnsigned: true, isBigEndian: true);
            var s = new BigInteger(sequence.ReadIntegerBytes().Span, isUnsigned: true, isBigEndian: true);
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence()) { writer.WriteInteger(r); writer.WriteInteger(s); }
            return writer.Encode();
        }
        catch (AsnContentException) { throw new InvalidDataException("The signer returned something that is not an ECDSA signature."); }
    }

    /// <summary>Signs; <see cref="Sign"/> checks the result.</summary>
    protected abstract byte[] SignData(byte[] data);

    /// <summary>A signer over a P-256 private key in PEM.</summary>
    internal static PackSigner FromPem(string privateKeyPem) => new PemSigner(privateKeyPem);

    /// <summary>
    /// A signer that runs <paramref name="command"/> through the shell, writes the bytes to sign to its standard input, and
    /// reads the signature from its standard output, base64: DER, or the 64-byte r‖s form that KMS and PKCS#11 tools print.
    /// </summary>
    internal static PackSigner FromCommand(string command, string publicKeyPem) => new CommandSigner(command, publicKeyPem);

    /// <inheritdoc/>
    public virtual void Dispose() { }

    private sealed class PemSigner : PackSigner
    {
        private readonly ECDsa _key = ECDsa.Create();

        internal PemSigner(string privateKeyPem)
        {
            _key.ImportFromPem(privateKeyPem);
            if (!IsP256(_key)) throw new ArgumentException("The key must be a P-256 key.", nameof(privateKeyPem));
            PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
        }

        internal override string PublicKeyPem { get; }
        protected override byte[] SignData(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        public override void Dispose() => _key.Dispose();
    }

    private sealed class CommandSigner(string command, string publicKeyPem) : PackSigner
    {
        internal override string PublicKeyPem { get; } = publicKeyPem;

        protected override byte[] SignData(byte[] data)
        {
            // cmd.exe takes the command line as written (/s strips the outer quotes), not argv-escaped; sh takes it as one argument.
            var start = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"") : new ProcessStartInfo("/bin/sh");
            if (!OperatingSystem.IsWindows()) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(command); }
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new InvalidDataException("The signer command did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.BaseStream.Write(data);
            process.StandardInput.Close();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidDataException("The signer command did not finish in 120 s.");
            }
            if (process.ExitCode != 0) throw new InvalidDataException($"The signer command failed ({process.ExitCode}): {error.Result.Trim()}");
            byte[] signature;
            try { signature = Convert.FromBase64String(output.Result.Trim()); }
            catch (FormatException) { throw new InvalidDataException("The signer command must print the signature in base64."); }
            return signature.Length == 64 ? Der(signature) : signature;
        }

        // The 64-byte r||s form as the DER SEQUENCE { INTEGER r, INTEGER s } that the pack format stores.
        private static byte[] Der(byte[] p1363)
        {
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(new BigInteger(p1363.AsSpan(0, 32), isUnsigned: true, isBigEndian: true));
                writer.WriteInteger(new BigInteger(p1363.AsSpan(32, 32), isUnsigned: true, isBigEndian: true));
            }
            return writer.Encode();
        }
    }
}
