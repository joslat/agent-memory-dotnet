#pragma warning disable AMEXT001
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ampack;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Federated packs end to end on a real Solid server and a real Neo4j (federated packs §8, FED-e2e). A supplier binds its
/// signing key in its WebID profile and publishes the real martial arts pack for one buyer; the buyer's host enrols the key
/// by its id, discovers the pack from the supplier's WebID alone, fetches it, verifies pack and receipt, installs it through
/// the normal path, imports it into Neo4j and finds a kata. A stranger and anyone else see nothing and the Pod refuses them
/// the bytes, even though the supplier's Pod is shared with everyone signed in. A federated set travels the same way. The host
/// refuses an archive swapped in the Pod, an edited receipt, a copy offered from another place, and a key the supplier
/// withdrew from its profile; none of those reaches the store. All of it on a server that enforces access with Web Access
/// Control and on one that enforces it with Access Control Policies.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    // Both access-control models Solid servers implement: Web Access Control and Access Control Policies.
    [Theory]
    [InlineData("wac")]
    [InlineData("acp")]
    public async Task AFederatedPackTravelsFromTheSuppliersPodIntoTheBuyersStoreAndEveryForgeryOrWrongPlaceIsRefused(string accessModel)
    {
        await using var server = await SolidTestServer.StartAsync(accessModel);
        var options = new SolidOptions { AllowLoopbackHttp = true };
        using var supplier = await server.LoginAsync("supplier", options);
        using var buyer = await server.LoginAsync("buyer", options);
        using var stranger = await server.LoginAsync("stranger", options);
        var key = AgentMemory.Packs.Format.PackKey.Create();
        var method = await SolidPacks.BindKeyAsync(supplier, key.PublicKeyPem);
        // A Pod-wide share above the pack container: everyone signed in may read all of the supplier's Pod. WAC lets a
        // resource's own rules override it; ACP adds it to them, so the pack's own rules must deny it (FED-review round 2 M-A).
        await ShareWithEveryoneSignedIn(supplier, $"{server.BaseUrl}/supplier/", accessModel);
        await Put(supplier, $"{server.BaseUrl}/supplier/notes/shared.txt", "for everyone signed in"u8.ToArray());
        (await stranger.SendAsync(HttpMethod.Get, $"{server.BaseUrl}/supplier/notes/shared.txt")).Status.Should().Be(HttpStatusCode.OK, "the share is in force");

        // The buyer's host reads the supplier's profile and binds the one key it names, for its namespace, to that WebID.
        var asserted = (await buyer.ProfileAsync(supplier.WebId!)).Keys.Should().ContainSingle().Subject;
        asserted.KeyId.Should().Be(key.KeyId);
        var buyerPolicyJson = TrustJson(asserted, supplier.WebId!, buyer.WebId!);
        var buyerPolicy = TrustPolicy.Parse(buyerPolicyJson);

        // The real martial arts pack, federated for the buyer alone, built and signed by the supplier.
        var source = new Dictionary<string, byte[]>(PackAuthoring.FromModuleBundle(RealBundle("MartialArts")), StringComparer.Ordinal);
        Federate(source, supplier.WebId!, buyer.WebId!);
        var archive = PackAuthoring.Build(source, key.PrivateKeyPem);
        using var signer = PackSigner.FromPem(key.PrivateKeyPem);

        var publication = await SolidPacks.PublishAsync(supplier, archive, signer, [buyer], [stranger]);
        publication.AccessModel.Should().Be(accessModel);
        publication.Denied.Should().Equal(PublicationReceipt.Anonymous, stranger.WebId);
        publication.Allowed.Should().Equal(buyer.WebId);

        // Discovery from the WebID alone: the buyer finds it; the stranger and anyone else find nothing, and the Pod refuses
        // them the bytes even when they know the URL.
        var offered = await SolidPacks.DiscoverAsync(buyer, supplier.WebId!);
        offered.Should().ContainSingle().Which.Should().Match<CatalogEntry>(e => e.Id == "io.agentmemory.packs.martialarts" && e.Archive == publication.Archive);
        (await SolidPacks.DiscoverAsync(stranger, supplier.WebId!)).Should().BeEmpty();
        using (var anyone = SolidSession.Anonymous(options))
        {
            (await SolidPacks.DiscoverAsync(anyone, supplier.WebId!)).Should().BeEmpty();
            (await anyone.SendAsync(HttpMethod.Get, publication.Archive)).Status.Should().Be(HttpStatusCode.Unauthorized);
        }
        (await stranger.SendAsync(HttpMethod.Get, publication.Archive)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.SendAsync(HttpMethod.Get, publication.Receipt)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await stranger.SendAsync(HttpMethod.Get, publication.Catalogs[0])).Status.Should().Be(HttpStatusCode.Forbidden, "a reader's catalog is that reader's alone");
        (await stranger.SendAsync(HttpMethod.Get, publication.Container)).Status.Should().Be(HttpStatusCode.Forbidden, "the pack container's listing is the owner's alone");
        using (var anyone = SolidSession.Anonymous(options))
            (await anyone.SendAsync(HttpMethod.Get, publication.Container)).Status.Should().Be(HttpStatusCode.Unauthorized, "the pack container's listing is the owner's alone");

        // Another account's credentials, given with the buyer's WebID, do not log in as the buyer: the token names its own WebID.
        await FluentActions.Awaiting(() => server.LoginAsync("supplier", options, asWebId: buyer.WebId!)).Should().ThrowAsync<InvalidDataException>().WithMessage("*not for*");

        // Fetched and verified as the buyer's host, installed through the normal path, imported into Neo4j, and found.
        var folder = Directory.CreateTempSubdirectory("ampack-federated-").FullName;
        try
        {
            var fetched = await SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy);
            fetched.Receipt.Location.Should().Be(publication.Archive);
            fetched.Receipt.Access.Should().Be(accessModel, "the receipt names the access model that held under the probe");
            var store = PackStore.Open(Path.Combine(folder, "store"));
            var installed = store.Install(fetched.Archive, PackTrust.Parse(buyerPolicyJson));
            installed.Digest.Should().Be(fetched.Pack.Digest);
            var loaded = PackLoader.Load(Path.Combine(folder, "store", installed.File), PackTrust.Parse(buyerPolicyJson));
            var importer = new KnowledgePackImporter(new Neo4jKnowledgePackStore(fixture.TransactionRunner));
            var count = loaded.Knowledge!.Proposals.Count;
            (await importer.ImportAsync(loaded.Knowledge)).Created.Should().Be(count);
            using var services = Policies();
            var reader = new Neo4jModuleReader(fixture.TransactionRunner, loaded.Knowledge.Resolution, loaded.Id, services.GetRequiredService<IMemoryIsolationPolicy>());
            var kata = await new ModuleViews(loaded.Knowledge.Resolution, reader)
                .EvaluateWithArgumentsAsync("kata", new() { Search = "Heian Shodan" }, new ModuleReadContext { Scope = MemoryScope.For("buyer") });
            kata.Items.Should().Contain(i => i.Record.Key == "shotokan/heian-shodan").Which.Record.Source.Pack!.Digest.Should().Be(fetched.Pack.Digest);

            // A federated set carrying the pack: published with its member (FED-review round 2 M-D), found, fetched whole.
            var setPublication = await SolidPacks.PublishAsync(supplier, FederatedSet(archive, key, supplier.WebId!, buyer.WebId!), signer, [buyer], [stranger]);
            setPublication.Denied.Should().Equal(PublicationReceipt.Anonymous, stranger.WebId);
            var setEntry = (await SolidPacks.DiscoverAsync(buyer, supplier.WebId!)).Should().HaveCount(2).And.ContainSingle(e => e.Id == "io.agentmemory.packs.bundle").Subject;
            var fetchedSet = await SolidPacks.FetchAsync(buyer, setEntry, buyerPolicy);
            fetchedSet.Pack.Manifest.Kind.Should().Be("set");
            fetchedSet.Receipt.Location.Should().Be(setPublication.Archive);

            // A re-publication that fails after it rewrote the archive's rules (a reader probe outside the audience) puts every
            // rule back from the server's own text: the buyer still fetches, the stranger is still refused (FED-review round 4).
            (await FluentActions.Awaiting(() => SolidPacks.PublishAsync(supplier, archive, signer, [stranger], [])).Should().ThrowAsync<InvalidDataException>())
                .Which.Message.Should().Contain("not in the audience").And.NotContain("WITHDRAWAL FAILED");
            (await SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy)).Pack.Digest.Should().Be(fetched.Pack.Digest);
            (await stranger.SendAsync(HttpMethod.Get, publication.Archive)).Status.Should().Be(HttpStatusCode.Forbidden);

            // 1. An archive swapped in the Pod: a valid, signed pack, but not the bytes the receipt names.
            var swapped = Encoding.UTF8.GetString(source["records/kata.jsonl"]).Replace("peaceful mind", "calm mind", StringComparison.Ordinal);
            var changed = new Dictionary<string, byte[]>(source, StringComparer.Ordinal) { ["records/kata.jsonl"] = Encoding.UTF8.GetBytes(swapped) };
            await Put(supplier, publication.Archive, PackAuthoring.Build(changed, key.PrivateKeyPem));
            await Refused(SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy), "receipt");
            await Put(supplier, publication.Archive, archive);

            // 2. A receipt edited in the Pod: its signature no longer holds.
            var envelope = JsonNode.Parse(publication.ReceiptBytes)!.AsObject();
            var statement = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
            statement["predicate"]!["publishedAt"] = "2026-01-01T00:00:00Z"; // a consistent receipt, only not the one signed
            envelope["payload"] = Convert.ToBase64String(Jcs.Canonical(statement));
            await Put(supplier, publication.Receipt, Jcs.Canonical(envelope));
            await Refused(SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy), "does not verify");
            await Put(supplier, publication.Receipt, publication.ReceiptBytes);

            // 3. A copy offered from another place, with the real receipt: the receipt vouches for one URL only.
            var copy = publication.Container + "copies/" + PublicationReceipt.ArchiveNameFor(fetched.Pack.Digest);
            await Put(supplier, copy, archive);
            (await SolidPacks.RestrictAsync(supplier, copy, supplier.WebId!, [buyer.WebId!])).Should().Be(accessModel);
            await Refused(SolidPacks.FetchAsync(buyer, offered[0] with { Archive = copy }, buyerPolicy), "not at " + copy);

            // 4. The key withdrawn from the supplier's profile: the re-check refuses; without it the host's own binding decides.
            var patch = $"@prefix solid: <http://www.w3.org/ns/solid/terms#> .\n_:p a solid:InsertDeletePatch ; solid:deletes {{ <{supplier.WebId}> <https://w3id.org/security#assertionMethod> <{method}> . }} .\n";
            (await supplier.SendAsync(HttpMethod.Patch, SolidPacks.Document(supplier.WebId!), Encoding.UTF8.GetBytes(patch), "text/n3")).IsSuccess.Should().BeTrue();
            await Refused(SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy), "no longer asserts");
            (await SolidPacks.FetchAsync(buyer, offered[0], buyerPolicy, recheck: false)).Pack.Digest.Should().Be(fetched.Pack.Digest);

            // 5. The same bytes on a stranger's host: the audience refuses them before any store sees them.
            var strangerPolicy = TrustPolicy.Parse(TrustJson(asserted, supplier.WebId!, stranger.WebId!));
            PackVerifier.Verify(PackReader.ReadArchive(archive, "copy"), strangerPolicy, DateTimeOffset.UtcNow).Problems
                .Should().ContainSingle(p => p.Contains("this host is " + stranger.WebId, StringComparison.Ordinal));

            // None of the refusals wrote anything: the store holds exactly the one import.
            (await importer.GetStateAsync(loaded.Manifest)).LiveRecords.Should().Be(count);
            (await importer.RemoveAsync(loaded.Manifest)).Closed.Should().Be(count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Federate(Dictionary<string, byte[]> source, string publisher, string reader)
    {
        var manifest = JsonNode.Parse(source["pack.json"])!.AsObject();
        manifest["extensions"] = new JsonObject
        {
            ["critical"] = new JsonArray("federation"),
            ["federation"] = new JsonObject
            {
                ["format"] = PackFederation.Format, ["publisher"] = publisher, ["audience"] = new JsonArray(reader), ["purpose"] = "Karate study notes for one student",
            },
        };
        source["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
    }

    // A federated set for one reader that carries the federated pack as its member, signed by the same key.
    private static byte[] FederatedSet(byte[] member, AgentMemory.Packs.Format.PackKey key, string publisher, string reader)
    {
        var memberFiles = PackReader.ReadArchive(member, "member").Files;
        var memberId = JsonNode.Parse(memberFiles["pack.json"])!["id"]!.GetValue<string>();
        var manifest = new JsonObject { ["format"] = "agentmemory.pack/1.2", ["id"] = "io.agentmemory.packs.bundle", ["version"] = "1.0.0", ["kind"] = "set", ["contains"] = new JsonArray(memberId) };
        var set = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["pack.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()) };
        Federate(set, publisher, reader);
        foreach (var (path, bytes) in memberFiles) set[$"packs/{memberId}/{path}"] = bytes;
        var (files, _) = PackBuilder.Build(set);
        PackBuilder.Sign(files, key.PrivateKeyPem, "publisher", DateTimeOffset.UtcNow);
        return PackBuilder.Pack(files);
    }

    // Everyone signed in may read the whole Pod: one more rule in the access resource of the storage root, kept beside the
    // owner's own (the document is read and written back whole, so its relative IRIs keep their meaning).
    private static async Task ShareWithEveryoneSignedIn(SolidSession owner, string storage, string accessModel)
    {
        var acl = (await owner.SendAsync(HttpMethod.Head, storage)).Links("acl").First();
        var current = await owner.SendAsync(HttpMethod.Get, acl, accept: "text/turtle");
        current.IsSuccess.Should().BeTrue($"the owner reads the access rules of {storage}");
        const string acp = AccessModels.AcpNamespace;
        const string a = Vocabulary.Acl;
        string share;
        if (accessModel == "acp")
        {
            var root = Turtle.Parse(SolidSession.Text(current), acl).First(t => t.Predicate is acp + "accessControl" or acp + "resource").Subject.Value;
            share = $"\n<{root}> <{acp}memberAccessControl> <{acl}#signedIn> .\n<{acl}#signedIn> a <{acp}AccessControl> ; <{acp}apply> <{acl}#signedInPolicy> .\n"
                + $"<{acl}#signedInPolicy> a <{acp}Policy> ; <{acp}allow> <{a}Read> ; <{acp}anyOf> <{acl}#signedInMatcher> .\n"
                + $"<{acl}#signedInMatcher> a <{acp}Matcher> ; <{acp}agent> <{acp}AuthenticatedAgent> .\n";
        }
        else
            share = $"\n<{acl}#signedIn> a <{a}Authorization> ; <{a}agentClass> <{a}AuthenticatedAgent> ; <{a}accessTo> <{storage}> ; <{a}default> <{storage}> ; <{a}mode> <{a}Read> .\n";
        var written = await owner.SendAsync(HttpMethod.Put, acl, Encoding.UTF8.GetBytes(SolidSession.Text(current) + share), "text/turtle");
        written.IsSuccess.Should().BeTrue($"writing the access rules of {storage} gave {(int)written.Status}");
    }

    private static byte[] TrustJson(AssertedKey key, string publisher, string host) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["format"] = TrustPolicy.Format,
        ["keys"] = new JsonArray(new JsonObject
        {
            ["keyId"] = key.KeyId, ["publicKey"] = key.PublicKeyPem, ["idPrefixes"] = new JsonArray("io.agentmemory"), ["roles"] = new JsonArray("publisher"), ["webId"] = publisher,
        }),
        ["host"] = new JsonObject { ["webIds"] = new JsonArray(host) },
    }.ToJsonString());

    private static async Task Put(SolidSession owner, string url, byte[] body)
    {
        var response = await owner.SendAsync(HttpMethod.Put, url, body, url.EndsWith(".acl", StringComparison.Ordinal) ? "text/turtle" : "application/octet-stream");
        response.IsSuccess.Should().BeTrue($"writing {url} gave {(int)response.Status}");
    }

    private static async Task Refused(Task<FetchedPack> fetch, string why) =>
        (await FluentActions.Awaiting(() => fetch).Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain(why);

    /// <summary>A Community Solid Server in a container with three seeded test accounts and client credentials for each.</summary>
    private sealed class SolidTestServer : IAsyncDisposable
    {
        private const string Image = "solidproject/community-server:7";
        private static readonly string[] Accounts = ["supplier", "buyer", "stranger"];
        private readonly IContainer _container;
        private readonly Dictionary<string, (string Id, string Secret)> _credentials = new(StringComparer.Ordinal);

        private SolidTestServer(IContainer container, string baseUrl) { _container = container; BaseUrl = baseUrl; }

        internal string BaseUrl { get; }

        internal static async Task<SolidTestServer> StartAsync(string accessModel)
        {
            // WAC: the default in-memory server. ACP: the server's own file-backed ACP configuration, in a container folder.
            var config = accessModel == "acp" ? "config/file-acp.json" : "config/default.json";
            // The server's base URL must be the URL clients use, so the host port is chosen first and bound as is.
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            var seed = JsonSerializer.Serialize(Accounts.Select(a => new { email = a + "@example.test", password = a + "-test-only", pods = new[] { new { name = a } } }));
            var container = new ContainerBuilder()
                .WithImage(Image)
                .WithPortBinding(port, port)
                .WithResourceMapping(Encoding.UTF8.GetBytes(seed), "/seed/seed.json")
                .WithCommand("-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-b", $"http://localhost:{port}/", "-c", config,
                    "-f", "/tmp/data", "--seedConfig", "/seed/seed.json")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Listening to server at"))
                .Build();
            await container.StartAsync();
            var server = new SolidTestServer(container, $"http://localhost:{port}");
            try
            {
                using var http = new HttpClient();
                foreach (var account in Accounts)
                {
                    var login = await (await http.PostAsJsonAsync($"{server.BaseUrl}/.account/login/password/", new { email = account + "@example.test", password = account + "-test-only" }))
                        .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>();
                    using var index = new HttpRequestMessage(HttpMethod.Get, $"{server.BaseUrl}/.account/");
                    index.Headers.TryAddWithoutValidation("Authorization", "CSS-Account-Token " + login!["authorization"]!.GetValue<string>());
                    var controls = await (await http.SendAsync(index)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>();
                    using var create = new HttpRequestMessage(HttpMethod.Post, controls!["controls"]!["account"]!["clientCredentials"]!.GetValue<string>())
                    {
                        Content = JsonContent.Create(new { name = "ampack-" + account, webId = server.WebId(account) }),
                    };
                    create.Headers.TryAddWithoutValidation("Authorization", "CSS-Account-Token " + login["authorization"]!.GetValue<string>());
                    var credentials = await (await http.SendAsync(create)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonObject>();
                    server._credentials[account] = (credentials!["id"]!.GetValue<string>(), credentials["secret"]!.GetValue<string>());
                }
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }

        internal string WebId(string account) => $"{BaseUrl}/{account}/profile/card#me";

        internal Task<SolidSession> LoginAsync(string account, SolidOptions options, string? asWebId = null) =>
            SolidSession.LoginAsync(asWebId ?? WebId(account), _credentials[account].Id, _credentials[account].Secret, options);

        public async ValueTask DisposeAsync() => await _container.DisposeAsync();
    }
}
