using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Publishing on a Pod (FED-review rounds 2-3): a federated set publishes with its members; containers get their rules
/// before anything is placed in them and each new resource gets its rules before its bytes; any failure puts every resource
/// back exactly as it was, a live placement and its rules included; a narrower placement drops the pack from former readers'
/// catalogs; a type index this tool left unlinked is taken up and anyone else's is never overwritten. Driven by a Pod held in
/// memory: no network.
/// </summary>
public sealed class SolidPublishTests
{
    private const string Alice = "https://pod.example/alice/profile/card#me";
    private const string Storage = "https://pod.example/alice/";
    private const string Container = Storage + "ampack/";
    private const string Buyer = "https://buyer-b.example/profile/card#agent";
    private const string Other = "https://other.example/profile/card#me";
    private const string TypeIndex = Storage + "settings/publicTypeIndex.ttl";

    /// <summary>
    /// A Pod in memory: Alice's profile (an issuer, a storage, the key she asserts), her issuer, and what she writes. Signed-in
    /// requests are hers; anonymous ones read only the profile and the issuer. A WAC server: each resource names its .acl, and
    /// removing a resource removes its .acl. A container is created by a PUT on its URL, once.
    /// </summary>
    private sealed class MemoryPod(string publicKeyPem) : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Resources { get; } = new(StringComparer.Ordinal);
        internal List<string> Seen { get; } = [];
        internal List<(string Url, int Length)> Puts { get; } = [];
        internal Func<HttpRequestMessage, Exception?> Fail { get; set; } = _ => null;
        internal bool IndexLinked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Seen.Add(request.Method.Method + " " + url);
            if (Fail(request) is { } failure) throw failure;
            var response = Answer(request, url);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private HttpResponseMessage Answer(HttpRequestMessage request, string url)
        {
            if (url == Storage + "profile/card" && request.Method == HttpMethod.Get)
                return Text($"<#me> <{Vocabulary.Solid}oidcIssuer> <https://pod.example/> ; <{Vocabulary.Pim}storage> <{Storage}> .\n"
                    + (IndexLinked ? $"<#me> <{Vocabulary.Solid}publicTypeIndex> <{TypeIndex}> .\n" : "")
                    + WebIdProfile.AssertionFor(Alice, publicKeyPem).Triples);
            if (url == "https://pod.example/.well-known/openid-configuration")
                return Text("""{"issuer":"https://pod.example/","token_endpoint":"https://pod.example/.oidc/token"}""", "application/json");
            if (url == "https://pod.example/.oidc/token")
                return Text(JsonSerializer.Serialize(new { access_token = Jwt(Alice), token_type = "DPoP", expires_in = 600 }), "application/json");
            if (request.Headers.Authorization is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var exists = Resources.ContainsKey(url);
            switch (request.Method.Method)
            {
                case "PUT":
                    if (url.EndsWith('/') && exists) return new HttpResponseMessage(HttpStatusCode.Conflict);
                    if (request.Headers.IfNoneMatch.Any(t => t.Tag == "*") && exists) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                    Resources[url] = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    Puts.Add((url, Resources[url].Length));
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "DELETE":
                    return new HttpResponseMessage(Resources.Remove(url) | Resources.Remove(url + ".acl") ? HttpStatusCode.ResetContent : HttpStatusCode.NotFound);
                case "PATCH":
                    // Only the profile's link to its type index is kept: what the next publication reads.
                    if (url == Storage + "profile/card" && request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("publicTypeIndex", StringComparison.Ordinal))
                        IndexLinked = true;
                    return new HttpResponseMessage(HttpStatusCode.ResetContent);
                case "HEAD":
                    if (!exists) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    if (!url.EndsWith(".acl", StringComparison.Ordinal)) head.Headers.Add("Link", $"<{url}.acl>; rel=\"acl\"");
                    return head;
                default:
                    if (url.EndsWith('/') && exists)
                        return Text(string.Concat(Resources.Keys.Where(k => k != url && k.StartsWith(url, StringComparison.Ordinal) && !k.EndsWith(".acl", StringComparison.Ordinal))
                            .Select(k => url + k[url.Length..].Split('/')[0] + (k[url.Length..].Contains('/') ? "/" : "")).Distinct()
                            .Select(child => $"<{url}> <{Vocabulary.Ldp}contains> <{child}> .\n")));
                    return Resources.TryGetValue(url, out var bytes)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Text(string text, string type = "text/turtle") => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };

        private static string Jwt(string webId) =>
            Base64Url.Encode("{\"alg\":\"ES256\"}"u8) + "." + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { webid = webId })) + "." + Base64Url.Encode("sig"u8);
    }

    private static string Federation(params string[] audience) =>
        ",\"extensions\":{\"critical\":[\"federation\"],\"federation\":{\"format\":\"agentmemory.pack.federation/1\",\"publisher\":\"" + Alice
        + "\",\"audience\":[" + string.Join(",", audience.Select(a => "\"" + a + "\"")) + "]}}";

    private static byte[] FederatedPack(SamplePack sample, params string[] audience)
    {
        var source = sample.Source();
        source["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"{{sample.Id}}","version":"{{sample.Version}}","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT"{{Federation(audience)}}}""");
        var (files, _) = PackBuilder.Build(source);
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        return PackBuilder.Pack(files);
    }

    // A set whose member's id is not under the set's: io.example.bundle carries io.example.member.
    private static byte[] FederatedSet(SamplePack sample)
    {
        var member = new SamplePack("io.example.member", "1.0.0");
        var source = member.Source();
        source["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"io.example.member","version":"1.0.0","kind":"knowledge","sdk":"[0.1,0.2)","tier":"open","licence":"MIT"{{Federation(Buyer)}}}""");
        var (memberFiles, _) = PackBuilder.Build(source);
        PackBuilder.Sign(memberFiles, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        var set = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["pack.json"] = SamplePack.Utf8($$"""{"format":"agentmemory.pack/1.2","id":"io.example.bundle","version":"1.0.0","kind":"set","contains":["io.example.member"]{{Federation(Buyer)}}}"""),
        };
        foreach (var (path, bytes) in memberFiles) set["packs/io.example.member/" + path] = bytes;
        var (files, _) = PackBuilder.Build(set);
        PackBuilder.Sign(files, sample.Key.PrivateKeyPem, "publisher", SamplePack.SignedAt);
        return PackBuilder.Pack(files);
    }

    private static async Task<(MemoryPod Pod, SolidSession Owner, PackSigner Signer)> Alices(SamplePack sample)
    {
        var pod = new MemoryPod(sample.Key.PublicKeyPem);
        return (pod, await SolidSession.LoginAsync(Alice, "id", "secret", handler: pod), PackSigner.FromPem(sample.Key.PrivateKeyPem));
    }

    // What a pack container holds besides containers and rules: the archives, receipts and catalogs.
    private static IEnumerable<string> Placed(MemoryPod pod) =>
        pod.Resources.Keys.Where(k => k.StartsWith(Container, StringComparison.Ordinal) && !k.EndsWith(".acl", StringComparison.Ordinal) && !k.EndsWith('/'));

    [Fact]
    public async Task A_federated_set_publishes_with_members_whose_ids_are_not_under_the_sets()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var publication = await SolidPacks.PublishAsync(owner, FederatedSet(sample), signer, [], []);

            publication.Denied.Should().Equal(PublicationReceipt.Anonymous);
            pod.Resources.Should().ContainKey(publication.Archive).And.ContainKey(publication.Receipt).And.ContainKey(publication.Catalogs.Single());
            Encoding.UTF8.GetString(pod.Resources[publication.Catalogs.Single()]).Should().Contain("io.example.bundle");
        }
    }

    [Fact]
    public async Task Containers_get_their_rules_first_and_each_new_resource_its_rules_before_its_bytes()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var publication = await SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], []);

            var firstMember = pod.Puts.FindIndex(p => Placed(pod).Contains(p.Url) || p.Url.StartsWith(Container + "sha256-", StringComparison.Ordinal));
            pod.Puts.FindIndex(p => p.Url == Container + ".acl").Should().BeLessThan(firstMember, "the container is restricted before anything is placed in it");
            pod.Puts.FindIndex(p => p.Url == Container + "for/.acl").Should().BeLessThan(pod.Puts.FindIndex(p => p.Url == publication.Catalogs[0]));
            foreach (var url in (string[])[publication.Archive, publication.Receipt, publication.Catalogs[0]])
            {
                var writes = pod.Puts.Select((p, i) => (p, i)).Where(x => x.p.Url == url).ToList();
                var rules = pod.Puts.FindIndex(p => p.Url == url + ".acl");
                writes.Should().HaveCount(2, url + " is created empty, then filled");
                writes[0].p.Length.Should().Be(0);
                rules.Should().BeInRange(writes[0].i + 1, writes[1].i - 1, "its rules are written between the two");
            }
        }
    }

    [Theory]
    [InlineData(".receipt.dsse.json")]          // the receipt's first write fails: it is removed too, recorded before the request
    [InlineData("/ampack/for/")]                // a catalog's first write fails: the archive and the receipt go as well
    public async Task Any_failure_on_a_first_publication_removes_what_it_placed(string failing)
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            pod.Fail = r => r.Method == HttpMethod.Put && r.RequestUri!.ToString() is var u && u.Contains(failing, StringComparison.Ordinal)
                && !u.EndsWith(".acl", StringComparison.Ordinal) && !u.EndsWith('/') ? new IOException("The connection was reset.") : null;

            await FluentActions.Awaiting(() => SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], [])).Should().ThrowAsync<IOException>();

            Placed(pod).Should().BeEmpty();
            pod.Seen.Should().Contain(s => s.StartsWith("DELETE ", StringComparison.Ordinal) && s.EndsWith(".receipt.dsse.json", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("archive rules")]               // narrowing the archive's rules fails
    [InlineData("receipt")]                     // the new receipt's write fails, after the archive's rules were narrowed
    [InlineData("former reader")]               // taking the pack out of the former reader's catalog fails, at the very end
    public async Task A_failed_republication_leaves_the_live_placement_exactly_as_it_was(string failing)
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var archive = FederatedPack(sample, Buyer, Other);
            var wide = await SolidPacks.PublishAsync(owner, archive, signer, [], []);
            var live = pod.Resources.ToDictionary(r => r.Key, r => r.Value.ToArray(), StringComparer.Ordinal);
            var former = Container + "for/" + SolidPacks.ReaderKey(Other) + ".ttl";
            var target = failing switch { "archive rules" => wide.Archive + ".acl", "receipt" => wide.Receipt, _ => former };
            var failed = false; // a transient failure: once, then the server answers again (and the undo goes through)
            pod.Fail = r =>
            {
                if (failed || r.Method != HttpMethod.Put || r.RequestUri!.ToString() != target) return null;
                failed = true;
                return new HttpRequestException("The server answered 503.");
            };

            await FluentActions.Awaiting(() => SolidPacks.PublishAsync(owner, archive, signer, [], [], [Buyer])).Should().ThrowAsync<HttpRequestException>();

            pod.Resources.Should().BeEquivalentTo(live, "the archive, the receipt, every catalog and every rule are as before");
        }
    }

    [Fact]
    public async Task A_narrower_placement_drops_the_pack_from_former_readers_catalogs_which_keep_their_rules()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var archive = FederatedPack(sample, Buyer, Other);
            var wide = await SolidPacks.PublishAsync(owner, archive, signer, [], []);
            wide.Catalogs.Should().HaveCount(2);

            var narrow = await SolidPacks.PublishAsync(owner, archive, signer, [], [], [Buyer]);

            narrow.Catalogs.Should().ContainSingle();
            var former = wide.Catalogs.Single(c => !narrow.Catalogs.Contains(c));
            Encoding.UTF8.GetString(pod.Resources[former]).Should().NotContain(sample.Id, "the former reader's catalog lists it no more");
            pod.Resources.Should().ContainKey(former + ".acl", "the catalog was written, not removed, so it keeps its rules");
            Encoding.UTF8.GetString(pod.Resources[narrow.Catalogs[0]]).Should().Contain(sample.Id);
        }
    }

    [Fact]
    public async Task A_type_index_this_tool_wrote_but_could_not_link_is_taken_up()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            pod.Resources[TypeIndex] = Encoding.UTF8.GetBytes($"<{TypeIndex}> a <{Vocabulary.Solid}TypeIndex>, <{Vocabulary.Solid}ListedDocument> .\n");

            await SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], []);

            pod.IndexLinked.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_type_index_of_someone_elses_already_there_but_not_linked_is_never_overwritten()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var theirs = Encoding.UTF8.GetBytes($"<{TypeIndex}> a <{Vocabulary.Solid}TypeIndex> .\n<#mine> a <{Vocabulary.Solid}TypeRegistration> ; <{Vocabulary.Solid}forClass> <https://schema.org/Book> .\n");
            pod.Resources[TypeIndex] = theirs;

            await FluentActions.Awaiting(() => SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], []))
                .Should().ThrowAsync<InvalidDataException>().WithMessage("*exists but*");

            pod.Resources[TypeIndex].Should().Equal(theirs);
            Placed(pod).Should().BeEmpty("the catalog type is registered before anything is placed");
        }
    }
}
