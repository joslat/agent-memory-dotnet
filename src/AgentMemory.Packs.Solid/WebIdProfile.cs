using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Packs.Format;

namespace AgentMemory.Packs.Solid;

/// <summary>The vocabularies a federated pack reads and writes on the Web.</summary>
internal static class Vocabulary
{
    internal const string Solid = "http://www.w3.org/ns/solid/terms#";
    internal const string Pim = "http://www.w3.org/ns/pim/space#";
    internal const string Ldp = "http://www.w3.org/ns/ldp#";
    internal const string Acl = "http://www.w3.org/ns/auth/acl#";
    internal const string Foaf = "http://xmlns.com/foaf/0.1/";
    /// <summary>Controlled Identifiers v1.0 and the security vocabulary: verification methods and their keys.</summary>
    internal const string Sec = "https://w3id.org/security#";
    internal const string Dct = "http://purl.org/dc/terms/";
    internal const string Prov = "http://www.w3.org/ns/prov#";
    /// <summary>The memory pack vocabulary; to register on w3id.org after the filing (federated packs §6).</summary>
    internal const string Ampack = "https://w3id.org/ampack/ns#";
    internal const string RdfJson = Turtle.Rdf + "JSON";
}

/// <summary>A P-256 public key a WebID asserts (<c>sec:assertionMethod</c>), as the trust policy pins it.</summary>
internal sealed record AssertedKey(string Method, string KeyId, string PublicKeyPem);

/// <summary>
/// What a WebID's profile document says that a federated pack needs (federated packs §4, §6): the OIDC issuers it logs in
/// with, its storage, its public type index, and the P-256 keys it asserts for signing.
/// </summary>
internal sealed record WebIdProfile(string WebId, IReadOnlyList<string> Issuers, IReadOnlyList<string> Storages, string? PublicTypeIndex,
    IReadOnlyList<AssertedKey> Keys)
{
    internal static WebIdProfile Read(string webId, IReadOnlyList<RdfTriple> triples)
    {
        var me = RdfTerm.Iri(webId);
        IEnumerable<RdfTerm> Objects(RdfTerm subject, string predicate) => triples.Where(t => t.Subject == subject && t.Predicate == predicate).Select(t => t.Object);
        var issuers = Objects(me, Vocabulary.Solid + "oidcIssuer").Where(o => o.Kind == RdfTermKind.Iri).Select(o => o.Value).Distinct().ToList();
        var storages = Objects(me, Vocabulary.Pim + "storage").Where(o => o.Kind == RdfTermKind.Iri).Select(o => o.Value).Distinct().ToList();
        var typeIndex = Objects(me, Vocabulary.Solid + "publicTypeIndex").Where(o => o.Kind == RdfTermKind.Iri).Select(o => o.Value).FirstOrDefault();
        var keys = new List<AssertedKey>();
        foreach (var method in Objects(me, Vocabulary.Sec + "assertionMethod").Distinct())
        {
            // A controller other than this WebID makes the method someone else's key: not this identity's.
            var controllers = Objects(method, Vocabulary.Sec + "controller").ToList();
            if (controllers.Count > 0 && controllers.Any(c => !c.IsIri(webId))) continue;
            foreach (var jwk in Objects(method, Vocabulary.Sec + "publicKeyJwk").Where(o => o.Kind == RdfTermKind.Literal))
                if (Jwk.ToPem(jwk.Value) is { } pem) keys.Add(new AssertedKey(method.Value, PackKey.KeyIdOf(pem), pem));
        }
        return new WebIdProfile(webId, issuers, storages, typeIndex, keys.DistinctBy(k => k.KeyId).ToList());
    }

    /// <summary>The Turtle that asserts <paramref name="publicKeyPem"/> for <paramref name="webId"/> (an N3 Patch inserts it).</summary>
    internal static (string MethodIri, string Triples) AssertionFor(string webId, string publicKeyPem)
    {
        var keyId = PackKey.KeyIdOf(publicKeyPem);
        var method = (webId.Contains('#', StringComparison.Ordinal) ? webId[..webId.IndexOf('#', StringComparison.Ordinal)] : webId) + "#ampack-" + keyId[..16];
        var triples = $"<{webId}> <{Vocabulary.Sec}assertionMethod> <{method}> .\n"
            + $"<{method}> a <{Vocabulary.Sec}JsonWebKey> ; <{Vocabulary.Sec}controller> <{webId}> ;\n"
            + $"    <{Vocabulary.Sec}publicKeyJwk> \"{TurtleText.Escape(Jwk.FromPem(publicKeyPem))}\"^^<{Vocabulary.RdfJson}> .\n";
        return (method, triples);
    }
}

/// <summary>P-256 public keys as JSON Web Keys (RFC 7517/7518): <c>{"crv","kty","x","y"}</c>, members in lexical order.</summary>
internal static class Jwk
{
    internal static string FromPem(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        var p = key.ExportParameters(false);
        if (p.Curve.Oid?.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException("Only a P-256 key is asserted for packs.");
        return $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.Encode(p.Q.X!)}\",\"y\":\"{Base64Url.Encode(p.Q.Y!)}\"}}";
    }

    /// <summary>The PEM of a P-256 JWK, or null for anything else (another curve, a private key, a malformed key).</summary>
    internal static string? ToPem(string jwk)
    {
        try
        {
            using var document = JsonDocument.Parse(jwk);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "kty") != "EC" || Text(root, "crv") != "P-256" || root.TryGetProperty("d", out _)) return null;
            if (Base64Url.Decode(Text(root, "x")) is not { Length: 32 } x || Base64Url.Decode(Text(root, "y")) is not { Length: 32 } y) return null;
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            return key.ExportSubjectPublicKeyInfoPem();
        }
        catch (Exception e) when (e is JsonException or CryptographicException or FormatException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[]? Decode(string? text)
    {
        if (text is null || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => null };
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}

/// <summary>Writing Turtle safely: IRIs come only from the WebID grammar (nothing to escape); literals are escaped.</summary>
internal static class TurtleText
{
    internal static string Escape(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text)
            builder.Append(c switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        return builder.ToString();
    }

    /// <summary>An IRI for writing inside &lt; &gt;: refused unless it needs no escaping there.</summary>
    internal static string Iri(string iri) =>
        iri.Length > 0 && iri.All(c => c > ' ' && c < '\u007F' && c is not ('<' or '>' or '"' or '{' or '}' or '|' or '\\' or '^' or '`'))
            ? iri : throw new InvalidDataException("An IRI with a character Turtle would need escaped is refused.");
}
