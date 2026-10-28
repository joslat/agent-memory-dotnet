using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// Publishing on a Pod (FED-review round 2): a federated set publishes with its members, any failure withdraws what was
/// placed and puts every catalog back, a narrower placement drops the pack from former readers' catalogs, and a type index
/// already there is never overwritten. Driven by a Pod held in memory: no network.
/// </summary>
public sealed class SolidPublishTests
{
    private const string Alice = "https://pod.example/alice/profile/card#me";
    private const string Storage = "https://pod.example/alice/";
    private const string Buyer = "https://buyer-b.example/profile/card#agent";
    private const string Other = "https://other.example/profile/card#me";
    private const string TypeIndex = Storage + "settings/publicTypeIndex.ttl";

    /// <summary>
    /// A Pod in memory: Alice's profile (an issuer, a storage, the key she asserts), her issuer, and what she writes. Signed-in
    /// requests are hers; anonymous ones read only the profile and the issuer. A WAC server: each resource names its .acl.
    /// </summary>
    private sealed class MemoryPod(string publicKeyPem) : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Resources { get; } = new(StringComparer.Ordinal);
        internal List<string> Seen { get; } = [];
        internal Func<HttpRequestMessage, Exception?> Fail { get; set; } = _ => null;
        private bool _indexLinked;

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
                    + (_indexLinked ? $"<#me> <{Vocabulary.Solid}publicTypeIndex> <{TypeIndex}> .\n" : "")
                    + WebIdProfile.AssertionFor(Alice, publicKeyPem).Triples);
            if (url == "https://pod.example/.well-known/openid-configuration")
                return Text("""{"issuer":"https://pod.example/","token_endpoint":"https://pod.example/.oidc/token"}""", "application/json");
            if (url == "https://pod.example/.oidc/token")
                return Text(JsonSerializer.Serialize(new { access_token = Jwt(Alice), token_type = "DPoP", expires_in = 600 }), "application/json");
            if (request.Headers.Authorization is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var exists = Resources.ContainsKey(url) || url.EndsWith('/') && Resources.Keys.Any(k => k.StartsWith(url, StringComparison.Ordinal));
            switch (request.Method.Method)
            {
                case "PUT":
                    if (request.Headers.IfNoneMatch.Any(t => t.Tag == "*") && exists) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                    Resources[url] = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "DELETE":
                    return new HttpResponseMessage(Resources.Remove(url) | Resources.Remove(url + ".acl") ? HttpStatusCode.ResetContent : HttpStatusCode.NotFound);
                case "PATCH":
                    // Only the profile's link to its type index is kept: what the next publication reads.
                    if (url == Storage + "profile/card" && request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("publicTypeIndex", StringComparison.Ordinal))
                        _indexLinked = true;
                    return new HttpResponseMessage(HttpStatusCode.ResetContent);
                case "HEAD":
                    if (!exists) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    if (!url.EndsWith(".acl", StringComparison.Ordinal)) head.Headers.Add("Link", $"<{url}.acl>; rel=\"acl\"");
                    return head;
                default:
                    if (url.EndsWith('/') && exists)
                        return Text(string.Concat(Resources.Keys.Where(k => k.StartsWith(url, StringComparison.Ordinal) && !k.EndsWith(".acl", StringComparison.Ordinal))
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

    [Theory]
    [InlineData(".receipt.dsse.json")]          // the receipt's write fails: it is withdrawn too, recorded before the request
    [InlineData("publicTypeIndex.ttl")]         // the last step fails: the archive, the receipt and the catalog all go
    public async Task Any_failure_withdraws_what_was_placed_and_puts_the_catalogs_back(string failing)
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            pod.Fail = r => r.Method == HttpMethod.Put && r.RequestUri!.ToString().EndsWith(failing, StringComparison.Ordinal) ? new IOException("The connection was reset.") : null;

            await FluentActions.Awaiting(() => SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], [])).Should().ThrowAsync<IOException>();

            pod.Resources.Keys.Should().NotContain(k => k.StartsWith(Storage + "ampack/", StringComparison.Ordinal) && !k.EndsWith(".acl", StringComparison.Ordinal));
            pod.Seen.Should().Contain(s => s.StartsWith("DELETE ", StringComparison.Ordinal) && s.EndsWith(".receipt.dsse.json", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_narrower_placement_drops_the_pack_from_former_readers_catalogs()
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
            pod.Resources.Should().NotContainKey(former, "the former reader's catalog listed only this pack");
            Encoding.UTF8.GetString(pod.Resources[narrow.Catalogs[0]]).Should().Contain(sample.Id);
        }
    }

    [Fact]
    public async Task A_type_index_already_there_but_not_linked_is_never_overwritten()
    {
        var sample = new SamplePack();
        var (pod, owner, signer) = await Alices(sample);
        using (owner)
        using (signer)
        {
            var theirs = "<#mine> a <http://www.w3.org/ns/solid/terms#TypeIndex> .\n"u8.ToArray();
            pod.Resources[TypeIndex] = theirs;

            await FluentActions.Awaiting(() => SolidPacks.PublishAsync(owner, FederatedPack(sample, Buyer), signer, [], []))
                .Should().ThrowAsync<InvalidDataException>().WithMessage("*exists but*");

            pod.Resources[TypeIndex].Should().Equal(theirs);
            pod.Resources.Keys.Should().NotContain(k => k.EndsWith(".ampack", StringComparison.Ordinal), "the publication was withdrawn");
        }
    }
}
