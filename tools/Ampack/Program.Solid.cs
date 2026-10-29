using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;

namespace Ampack;

/// <summary>
/// Federated packs on Solid Pods (federated packs §4–§7): <c>ampack solid bind-key|publish|discover|fetch</c> and
/// <c>ampack trust webid</c>. Client credentials come from the environment only (<c>AMPACK_SOLID_CLIENT_ID</c>,
/// <c>AMPACK_SOLID_CLIENT_SECRET</c>; a reader probe's from <c>AMPACK_PROBE_CLIENT_ID</c>, <c>AMPACK_PROBE_CLIENT_SECRET</c>; a
/// stranger probe's from <c>AMPACK_STRANGER_CLIENT_ID</c>, <c>AMPACK_STRANGER_CLIENT_SECRET</c>), never from arguments, and are
/// never printed.
/// </summary>
internal static partial class Program
{
    private static int Solid(Options o) => o.Positional(0, "solid action (bind-key, publish, discover or fetch)") switch
    {
        "bind-key" => SolidBindKey(o),
        "publish" => SolidPublish(o),
        "discover" => SolidDiscover(o),
        "fetch" => SolidFetch(o),
        var other => throw new UsageException($"Unknown solid action '{other}': bind-key, publish, discover or fetch."),
    };

    private static SolidOptions SolidOptions(Options o) => new() { AllowLoopbackHttp = o.Flag("allow-loopback-http") };

    // Text from the Web or from a pack (titles, purposes, ids, URLs, refusal messages that quote them) is shown, never
    // trusted: control and bidirectional characters become '?', and it is cut to a length without splitting a character
    // (FED-review: a title could carry terminal escapes).
    internal static string Shown(string text, int max = 200)
    {
        var cut = text.Length > max ? max - (char.IsHighSurrogate(text[max - 1]) ? 1 : 0) : text.Length;
        var shown = new string([.. text.Take(cut).Select(c => char.IsControl(c) || IsBidirectional(c) ? '?' : c)]);
        return cut < text.Length && max >= 64 ? shown + "…" : shown;
    }

    private static bool IsBidirectional(char c) => c is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    private static SolidSession Login(Options o, string webId, string prefix = "AMPACK_SOLID")
    {
        var id = Environment.GetEnvironmentVariable(prefix + "_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable(prefix + "_CLIENT_SECRET");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret))
            throw new UsageException($"Set {prefix}_CLIENT_ID and {prefix}_CLIENT_SECRET to the client credentials registered for {webId}.");
        return SolidSession.LoginAsync(webId, id, secret, SolidOptions(o)).GetAwaiter().GetResult();
    }

    private static int SolidBindKey(Options o)
    {
        var webId = o.Required("webid");
        using var owner = Login(o, webId);
        var method = SolidPacks.BindKeyAsync(owner, Text(o.Required("public"), "The public key")).GetAwaiter().GetResult();
        Console.WriteLine($"Key asserted in {webId}'s profile\n  method  {method}");
        return 0;
    }

    private static int SolidPublish(Options o)
    {
        var archive = PackReader.ReadArchiveFile(o.Positional(1, "pack.ampack"));
        var webId = o.Required("webid");
        using var owner = Login(o, webId);
        using var signer = Signer(o);
        var readers = new List<SolidSession>();
        var strangers = new List<SolidSession>();
        try
        {
            if (o.Value("probe-as") is { } probe) readers.Add(Login(o, probe, "AMPACK_PROBE"));
            if (o.Value("probe-stranger") is { } stranger) strangers.Add(Login(o, stranger, "AMPACK_STRANGER"));
            IReadOnlyList<string>? audience = o.Value("audience") is { } given ? List(given) : null;
            var publication = SolidPacks.PublishAsync(owner, archive, signer, readers, strangers, audience).GetAwaiter().GetResult();
            Console.WriteLine($"Published at {publication.Archive}\n  access  {publication.AccessModel}\n  receipt {publication.Receipt}\n  denied  {string.Join(", ", publication.Denied)}"
                + $"\n  allowed {(publication.Allowed.Count == 0 ? "(no reader probed)" : string.Join(", ", publication.Allowed))}");
            foreach (var catalog in publication.Catalogs) Console.WriteLine($"  catalog {catalog}");
            return 0;
        }
        finally { foreach (var session in readers.Concat(strangers)) session.Dispose(); }
    }

    private static int SolidDiscover(Options o)
    {
        var from = o.Required("from");
        using var reader = o.Value("webid") is { } webId ? Login(o, webId) : SolidSession.Anonymous(SolidOptions(o));
        var entries = SolidPacks.DiscoverAsync(reader, from).GetAwaiter().GetResult();
        if (entries.Count == 0) Console.WriteLine($"{from} offers {(reader.WebId is null ? "anonymous readers" : reader.WebId)} no packs.");
        foreach (var e in entries) Console.WriteLine($"  {Shown(e.Id)} {Shown(e.Version)}  {Shown(e.Digest, 23)}…  {Shown(e.Title ?? "")}\n      {Shown(e.Archive)}");
        return 0;
    }

    // Discover, fetch, verify pack and receipt as this host, then install through the normal path and log where it came from.
    private static int SolidFetch(Options o)
    {
        var from = o.Required("from");
        var webId = o.Required("webid");
        // The store first: a fetch installs into it, so this is where one may start (every other command needs it to exist).
        var storeFolder = o.Required("store");
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(storeFolder);
        var policy = Policy(o);
        if (!policy.HostWebIds.Contains(webId))
            throw new UsageException($"{webId} is not one of this host's WebIDs in the trust policy (host.webIds): a host fetches as itself.");
        using var reader = Login(o, webId);
        var entries = SolidPacks.DiscoverAsync(reader, from).GetAwaiter().GetResult();
        if (o.Value("id") is { } wanted) entries = [.. entries.Where(e => e.Id == wanted)];
        if (entries.Count == 0) return Report([$"{from} offers {webId} no {(o.Value("id") is { } id ? id : "packs")}."], "");
        var problems = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                var fetched = SolidPacks.FetchAsync(reader, entry, policy, recheck: !o.Flag("no-recheck")).GetAwaiter().GetResult();
                var installed = store.Install(fetched.Archive, Trust(o), o.Flag("allow-downgrade"));
                var log = new JsonObject
                {
                    ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    ["id"] = installed.Id, ["version"] = installed.Version, ["digest"] = installed.Digest, ["publisher"] = fetched.Pack.Manifest.Federation!.Publisher,
                    ["location"] = fetched.Location, ["receiptSha256"] = FilesList.Hex(fetched.ReceiptBytes), ["as"] = webId,
                };
                File.AppendAllText(Path.Combine(storeFolder, "federation.jsonl"), log.ToJsonString() + "\n");
                Console.WriteLine($"Fetched and installed {installed.Id} {installed.Version}\n  from    {fetched.Location}\n  digest  {installed.Digest}\n  placed  for {PackFederation.Describe(fetched.Receipt.Audience)} ({fetched.Receipt.Access}) at {fetched.Receipt.PublishedAt:u}");
            }
            // One entry that fails, however, leaves the others to be fetched (FED-review round 2).
            catch (Exception e) when (e is InvalidDataException or AgentMemory.Extensibility.Ampack.PackLoadException or HttpRequestException or IOException
                or TaskCanceledException or UriFormatException or JsonException)
            {
                problems.Add($"{entry.Id} {entry.Version}: " + (e is AgentMemory.Extensibility.Ampack.PackLoadException load ? string.Join(" ", load.Problems) : e.Message));
            }
        }
        return Report(problems, "");
    }

    // Enrolment (federated packs §4): read a WebID's profile, list the P-256 keys it asserts, and add one to the trust policy
    // only when the operator names its key id. Never trust on first use.
    private static int TrustCommand(Options o) => o.Positional(0, "trust action (add, list, remove, webid)") switch
    {
        "add" => TrustAdd(o),
        "list" => TrustList(o),
        "remove" => TrustRemove(o),
        "webid" => TrustWebId(o),
        var other => throw new UsageException($"Unknown trust action '{other}': add, list, remove or webid."),
    };

    // Review 1, I8: the trust policy kept from the command line, read back as a host reads it before it is written.
    private static JsonObject PolicyNode(string path) =>
        File.Exists(path) ? JsonNode.Parse(Text(path, "The trust policy", MaximumPolicyBytes))?.AsObject() ?? throw new UsageException($"{path} is not a JSON object.")
            : new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray() };

    private static void SavePolicy(string path, JsonObject policy)
    {
        var text = policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        TrustPolicy.Parse(System.Text.Encoding.UTF8.GetBytes(text));
        var partial = path + ".partial";
        File.WriteAllText(partial, text);
        File.Move(partial, path, overwrite: true);
    }

    private static int TrustAdd(Options o)
    {
        var path = o.Required("policy");
        var pem = Text(o.Required("public"), "The public key").Trim() + "\n";
        // Review 2: never a private key into a policy that gets shared, and one key per entry.
        if (!TrustPolicy.IsPublicKeyPem(pem))
            throw new UsageException($"{o.Required("public")} is not one PEM public key: give the .pub.pem file, never the private key.");
        var keyId = PackKey.KeyIdOf(pem);
        var roles = List(o.Value("role") ?? "publisher");
        if (o.Value("webid") is not null && roles.Contains("revoker")) throw new UsageException("A revoker key is never bound to a WebID.");
        var policy = PolicyNode(path);
        var keys = policy["keys"] as JsonArray ?? throw new UsageException($"{path} has no keys list.");
        if (keys.Any(k => k?["keyId"]?.GetValue<string>() == keyId)) throw new UsageException($"Key {Dsse.Short(keyId)} is already in {path}.");
        var entry = new JsonObject
        {
            ["keyId"] = keyId, ["publicKey"] = pem, ["idPrefixes"] = new JsonArray([.. List(o.Required("prefix")).Select(p => (JsonNode)p)]),
            ["roles"] = new JsonArray([.. roles.Select(r => (JsonNode)r)]),
        };
        if (o.Value("webid") is { } webId) entry["webId"] = webId;
        keys.Add(entry);
        SavePolicy(path, policy);
        Console.WriteLine($"Added key {Dsse.Short(keyId)} for {o.Required("prefix")} ({string.Join(", ", roles)}) to {path}. Check its id with its owner: {keyId}");
        return 0;
    }

    private static int TrustList(Options o)
    {
        var path = o.Required("policy");
        var policy = TrustPolicy.Parse(PackReader.ReadFile(path, MaximumPolicyBytes, "The trust policy"));
        Console.WriteLine($"{path}: {policy.Keys.Count} key(s); requires {string.Join(", ", policy.RequiredRoles)}");
        foreach (var key in policy.Keys)
            Console.WriteLine($"  {key.KeyId}  {string.Join(",", key.Roles)}  for {string.Join(", ", key.IdPrefixes)}"
                + (key.WebId is { } webId ? $"  bound to {Shown(webId)}" : "")
                + (key.NotAfter is { } notAfter ? $"  until {notAfter:u}" : ""));
        if (policy.RevokedKeys.Count > 0 || policy.RevokedDigests.Count > 0)
            Console.WriteLine($"  revoked: {policy.RevokedKeys.Count} key(s), {policy.RevokedDigests.Count} pack digest(s)");
        return 0;
    }

    private static int TrustRemove(Options o)
    {
        var path = o.Required("policy");
        var named = o.Positional(1, "key id (at least 16 characters of it)");
        if (named.Length < 16) throw new UsageException("Name the key by at least 16 characters of its id.");
        var policy = PolicyNode(path);
        var keys = policy["keys"] as JsonArray ?? throw new UsageException($"{path} has no keys list.");
        var matching = keys.Where(k => k?["keyId"]?.GetValue<string>() is { } id && id.StartsWith(named, StringComparison.Ordinal)).ToList();
        if (matching.Count != 1) throw new UsageException(matching.Count == 0 ? $"No key {named} in {path}." : $"{named} names {matching.Count} keys: give more of the id.");
        keys.Remove(matching[0]);
        SavePolicy(path, policy);
        Console.WriteLine($"Removed key {Dsse.Short(matching[0]!["keyId"]!.GetValue<string>())} from {path}. To refuse what it signed everywhere, revoke it instead (ampack revoke).");
        return 0;
    }

    private static int TrustWebId(Options o)
    {
        var webId = o.Positional(1, "WebID");
        using var session = SolidSession.Anonymous(SolidOptions(o));
        var profile = session.ProfileAsync(webId).GetAwaiter().GetResult();
        if (profile.Keys.Count == 0) return Report([$"{webId}'s profile asserts no P-256 key (sec:assertionMethod with a publicKeyJwk)."], "");
        Console.WriteLine($"{webId} asserts {profile.Keys.Count} key(s):");
        foreach (var key in profile.Keys) Console.WriteLine($"  {key.KeyId}  ({Shown(key.Method)})");
        if (o.Value("key") is not { } chosen)
        {
            Console.WriteLine("Nothing added: name the key you checked with the publisher, --key <keyId>.");
            return 0;
        }
        var asserted = profile.Keys.FirstOrDefault(k => k.KeyId == chosen) ?? throw new UsageException($"{webId} does not assert key {chosen}.");
        var policyPath = o.Required("policy");
        var role = o.Value("role") ?? "publisher";
        if (role == "revoker") throw new UsageException("A revoker key is never bound to a WebID.");
        var policy = PolicyNode(policyPath);
        var keys = policy["keys"] as JsonArray ?? throw new UsageException($"{policyPath} has no keys list.");
        if (keys.Any(k => k?["keyId"]?.GetValue<string>() == asserted.KeyId)) throw new UsageException($"Key {Dsse.Short(asserted.KeyId)} is already in {policyPath}.");
        keys.Add(new JsonObject
        {
            ["keyId"] = asserted.KeyId, ["publicKey"] = asserted.PublicKeyPem, ["idPrefixes"] = new JsonArray([.. List(o.Required("prefix")).Select(p => (JsonNode)p)]),
            ["roles"] = new JsonArray((JsonNode)role), ["webId"] = webId,
        });
        if (o.Value("host-webid") is { } hostWebId)
            policy["host"] = new JsonObject { ["webIds"] = new JsonArray([.. List(hostWebId).Select(w => (JsonNode)w)]) };
        SavePolicy(policyPath, policy);
        Console.WriteLine($"Added key {Dsse.Short(asserted.KeyId)} bound to {webId} for {o.Required("prefix")} ({role}) to {policyPath}.");
        return 0;
    }
}
