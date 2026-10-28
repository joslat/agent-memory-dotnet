using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;

namespace Ampack;

/// <summary>
/// Federated packs on Solid Pods (federated packs §4–§7): <c>ampack solid bind-key|publish|discover|fetch</c> and
/// <c>ampack trust webid</c>. Client credentials come from the environment only (<c>AMPACK_SOLID_CLIENT_ID</c>,
/// <c>AMPACK_SOLID_CLIENT_SECRET</c>; a probe identity's from <c>AMPACK_PROBE_CLIENT_ID</c>, <c>AMPACK_PROBE_CLIENT_SECRET</c>),
/// never from arguments, and are never printed.
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

    // Text from the Web (catalog titles, ids, URLs, profile IRIs) is shown, never trusted: control and bidirectional
    // characters become '?', and it is cut to a length (FED-review: a title could carry terminal escapes).
    internal static string Shown(string text, int max = 200)
    {
        var shown = new string([.. text.Take(max).Select(c => char.IsControl(c) || c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069' ? '?' : c)]);
        return text.Length > max && max >= 64 ? shown + "…" : shown;
    }

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
        var method = SolidPacks.BindKeyAsync(owner, File.ReadAllText(o.Required("public"))).GetAwaiter().GetResult();
        Console.WriteLine($"Key asserted in {webId}'s profile\n  method  {method}");
        return 0;
    }

    private static int SolidPublish(Options o)
    {
        var archive = File.ReadAllBytes(o.Positional(1, "pack.ampack"));
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
        var policy = Policy(o);
        if (!policy.HostWebIds.Contains(webId))
            throw new UsageException($"{webId} is not one of this host's WebIDs in the trust policy (host.webIds): a host fetches as itself.");
        var storeFolder = o.Required("store");
        var store = AgentMemory.Extensibility.Ampack.PackStore.Open(storeFolder);
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
            catch (Exception e) when (e is InvalidDataException or AgentMemory.Extensibility.Ampack.PackLoadException or HttpRequestException)
            {
                problems.Add($"{entry.Id} {entry.Version}: " + (e is AgentMemory.Extensibility.Ampack.PackLoadException load ? string.Join(" ", load.Problems) : e.Message));
            }
        }
        return Report(problems, "");
    }

    // Enrolment (federated packs §4): read a WebID's profile, list the P-256 keys it asserts, and add one to the trust policy
    // only when the operator names its key id. Never trust on first use.
    private static int TrustCommand(Options o) => o.Positional(0, "trust action (webid)") switch
    {
        "webid" => TrustWebId(o),
        var other => throw new UsageException($"Unknown trust action '{other}': webid."),
    };

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
        var policy = File.Exists(policyPath) ? JsonNode.Parse(File.ReadAllText(policyPath))?.AsObject() ?? throw new UsageException($"{policyPath} is not a JSON object.")
            : new JsonObject { ["format"] = "agentmemory.pack.trust/1.1", ["keys"] = new JsonArray() };
        var keys = policy["keys"] as JsonArray ?? throw new UsageException($"{policyPath} has no keys list.");
        if (keys.Any(k => k?["keyId"]?.GetValue<string>() == asserted.KeyId)) throw new UsageException($"Key {Dsse.Short(asserted.KeyId)} is already in {policyPath}.");
        keys.Add(new JsonObject
        {
            ["keyId"] = asserted.KeyId, ["publicKey"] = asserted.PublicKeyPem, ["idPrefixes"] = new JsonArray([.. List(o.Required("prefix")).Select(p => (JsonNode)p)]),
            ["roles"] = new JsonArray((JsonNode)role), ["webId"] = webId,
        });
        if (o.Value("host-webid") is { } hostWebId)
            policy["host"] = new JsonObject { ["webIds"] = new JsonArray([.. List(hostWebId).Select(w => (JsonNode)w)]) };
        var text = policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        TrustPolicy.Parse(System.Text.Encoding.UTF8.GetBytes(text)); // read back as a host reads it before it is written
        File.WriteAllText(policyPath, text);
        Console.WriteLine($"Added key {Dsse.Short(asserted.KeyId)} bound to {webId} for {o.Required("prefix")} ({role}) to {policyPath}.");
        return 0;
    }
}
