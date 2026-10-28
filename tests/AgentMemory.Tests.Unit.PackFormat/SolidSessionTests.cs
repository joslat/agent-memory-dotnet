using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The Solid session and the recipient's guards (FED-review round 1): the token names the WebID claimed, the issuer is the
/// profile's, DPoP proofs are well formed and nonces honoured, redirects stay on the origin, bodies stay bounded, and a
/// reader's requests only reach a publisher's own origins, in canonical form. Driven by a scripted handler: no network.
/// </summary>
public sealed class SolidSessionTests
{
    private const string WebIdOfAlice = "https://pod.example/alice/profile/card#me";
    private const string Issuer = "https://pod.example/";

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            var response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Text(string text, string type = "text/turtle", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, type) };

    private static string Jwt(object claims) =>
        Base64Url.Encode("{\"alg\":\"ES256\"}"u8) + "." + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims)) + "." + Base64Url.Encode("sig"u8);

    // A pod with a profile, an issuer configuration and a token endpoint, answering as the arguments say.
    private static Scripted Pod(string tokenWebId = WebIdOfAlice, string issuerNamed = Issuer, bool wantNonce = false)
    {
        var nonceAsked = false;
        return new Scripted(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://pod.example/alice/profile/card")
                return Text($"<#me> <http://www.w3.org/ns/solid/terms#oidcIssuer> <{Issuer}> .");
            if (url == "https://pod.example/.well-known/openid-configuration")
                return Text($$"""{"issuer":"{{issuerNamed}}","token_endpoint":"https://pod.example/.oidc/token"}""", "application/json");
            if (url == "https://pod.example/.oidc/token")
            {
                if (wantNonce && !nonceAsked)
                {
                    nonceAsked = true;
                    var refusal = Text("""{"error":"use_dpop_nonce"}""", "application/json", HttpStatusCode.BadRequest);
                    refusal.Headers.Add("DPoP-Nonce", "n-1");
                    return refusal;
                }
                return Text(JsonSerializer.Serialize(new { access_token = Jwt(new { webid = tokenWebId }), token_type = "DPoP", expires_in = 600 }), "application/json");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task A_login_holds_a_token_for_exactly_the_WebID_claimed()
    {
        using var session = await SolidSession.LoginAsync(WebIdOfAlice, "id", "secret", handler: Pod());

        session.WebId.Should().Be(WebIdOfAlice);
    }

    [Fact]
    public async Task Credentials_of_another_account_given_with_a_readers_WebID_are_refused()
    {
        var act = () => SolidSession.LoginAsync(WebIdOfAlice, "id", "secret", handler: Pod(tokenWebId: "https://pod.example/mallory/profile/card#me"));

        (await act.Should().ThrowAsync<InvalidDataException>()).Which.Message.Should().Contain("not for " + WebIdOfAlice).And.NotContain("secret");
    }

    [Fact]
    public async Task An_issuer_configuration_that_names_another_issuer_is_refused()
    {
        var act = () => SolidSession.LoginAsync(WebIdOfAlice, "id", "secret", handler: Pod(issuerNamed: "https://evil.example/"));

        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*names another issuer*");
    }

    [Fact]
    public async Task A_token_endpoint_that_asks_for_a_DPoP_nonce_gets_it_once()
    {
        var pod = Pod(wantNonce: true);
        using var session = await SolidSession.LoginAsync(WebIdOfAlice, "id", "secret", handler: pod);

        var proofs = pod.Seen.Where(r => r.RequestUri!.AbsolutePath == "/.oidc/token").Select(r => Claims(r.Headers.GetValues("DPoP").Single())).ToList();
        proofs.Should().HaveCount(2);
        proofs[0].TryGetProperty("nonce", out _).Should().BeFalse();
        proofs[1].GetProperty("nonce").GetString().Should().Be("n-1");
    }

    [Fact]
    public void A_token_that_is_not_a_JWT_names_no_WebID()
    {
        SolidSession.TokenWebId("opaque-token").Should().BeNull();
        SolidSession.TokenWebId(Jwt(new { sub = "x" })).Should().BeNull();
        SolidSession.TokenWebId(Jwt(new { webid = WebIdOfAlice })).Should().Be(WebIdOfAlice);
    }

    private static JsonElement Claims(string proof) => JsonDocument.Parse(Base64Url.Decode(proof.Split('.')[1])!).RootElement;

    [Fact]
    public void A_DPoP_proof_is_an_ES256_JWT_over_the_method_and_the_URL_without_query_or_fragment_bound_to_the_token()
    {
        using var session = SolidSession.Anonymous();
        var proof = session.Proof("GET", "https://pod.example/x?y=1#z", "the-token", "n-2");
        var parts = proof.Split('.');
        var header = JsonDocument.Parse(Base64Url.Decode(parts[0])!).RootElement;
        var claims = Claims(proof);

        header.GetProperty("typ").GetString().Should().Be("dpop+jwt");
        header.GetProperty("alg").GetString().Should().Be("ES256");
        claims.GetProperty("htm").GetString().Should().Be("GET");
        claims.GetProperty("htu").GetString().Should().Be("https://pod.example/x");
        claims.GetProperty("ath").GetString().Should().Be(Base64Url.Encode(SHA256.HashData("the-token"u8)));
        claims.GetProperty("nonce").GetString().Should().Be("n-2");
        claims.GetProperty("jti").GetString().Should().NotBeNullOrEmpty();
        var jwk = header.GetProperty("jwk");
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.Decode(jwk.GetProperty("x").GetString()), Y = Base64Url.Decode(jwk.GetProperty("y").GetString()) },
        });
        key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.Decode(parts[2])!, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue("the proof is signed by the key it names");
        jwk.TryGetProperty("d", out _).Should().BeFalse("the private part never leaves the session");
    }

    [Fact]
    public async Task Redirects_are_followed_on_the_same_origin_only()
    {
        var handler = new Scripted(request => request.RequestUri!.AbsolutePath switch
        {
            "/a" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/b", UriKind.Relative) } },
            "/b" => Text("ok", "text/plain"),
            "/away" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://internal.example/secret") } },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var session = SolidSession.Anonymous(handler: handler);

        var followed = await session.SendAsync(HttpMethod.Get, "https://pod.example/a");
        followed.Url.Should().Be("https://pod.example/b");
        Encoding.UTF8.GetString(followed.Body).Should().Be("ok");
        await FluentActions.Awaiting(() => session.SendAsync(HttpMethod.Get, "https://pod.example/away")).Should().ThrowAsync<InvalidDataException>().WithMessage("*another origin*");
        handler.Seen.Should().NotContain(r => r.RequestUri!.Host == "internal.example");
    }

    [Fact]
    public async Task A_body_beyond_its_bound_is_refused_whether_announced_or_streamed()
    {
        var handler = new Scripted(request => request.RequestUri!.AbsolutePath == "/announced"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[2048])) });
        using var session = SolidSession.Anonymous(handler: handler);

        await FluentActions.Awaiting(() => session.SendAsync(HttpMethod.Get, "https://pod.example/announced", limit: 1024)).Should().ThrowAsync<InvalidDataException>().WithMessage("*larger than*");
        await FluentActions.Awaiting(() => session.SendAsync(HttpMethod.Get, "https://pod.example/streamed", limit: 1024)).Should().ThrowAsync<InvalidDataException>().WithMessage("*larger than*");
    }

    [Theory]
    [InlineData("https://pod.example/alice/ampack/x.ampack", true)]
    [InlineData("https://pod.example/alice/ampack/%2e%2e/%2e%2e/bob/x.ampack", false)]
    [InlineData("https://pod.example/alice/ampack/%2E%2E/bob/x.ampack", false)]
    [InlineData("https://pod.example/alice/ampack/../bob/x.ampack", false)]
    [InlineData("https://pod.example/alice/./ampack/x.ampack", false)]
    [InlineData("https://pod.example/alice/ampack/x.ampack#f", false)]
    [InlineData("https://POD.example/alice/ampack/x.ampack", false)]             // a URI library would lowercase it: not canonical
    [InlineData("https://pod.example:443/alice/ampack/x.ampack", false)]
    [InlineData("https://pod.example/alice/ampack/x%41.ampack", false)]          // an escaped unreserved character
    public void Only_a_canonical_URL_is_followed_from_a_catalog(string url, bool canonical) => SolidPacks.IsCanonical(url).Should().Be(canonical);

    [Fact]
    public void A_catalog_entry_names_its_archive_and_receipt_exactly_by_its_digest_inside_the_container()
    {
        const string container = "https://pod.example/alice/ampack/";
        var digest = "sha256:" + new string('a', 64);
        var good = new CatalogEntry("io.example.notes", "1.0.0", digest, container + "sha256-" + new string('a', 64) + ".ampack",
            container + "sha256-" + new string('a', 64) + ".receipt.dsse.json", null);

        SolidPacks.IsCanonicalEntry(good, container).Should().BeTrue();
        SolidPacks.IsCanonicalEntry(good with { Archive = container + "other.ampack" }, container).Should().BeFalse();
        SolidPacks.IsCanonicalEntry(good with { Archive = "https://pod.example/bob/ampack/sha256-" + new string('a', 64) + ".ampack" }, container).Should().BeFalse();
        SolidPacks.IsCanonicalEntry(good with { Digest = "sha256:short" }, container).Should().BeFalse();
        SolidPacks.IsCanonicalEntry(good with { Id = "Not An Id" }, container).Should().BeFalse();
    }

    [Fact]
    public void A_readers_credentials_reach_only_the_publishers_own_origins()
    {
        var profile = new WebIdProfile(WebIdOfAlice, [Issuer], ["https://storage.example/alice/"], null, []);

        SolidPacks.PublisherOrigins(profile).Should().BeEquivalentTo("https://pod.example", "https://storage.example");
    }

    [Fact]
    public void Container_access_is_owner_only_in_both_models_and_covers_what_has_no_rules_of_its_own()
    {
        const string container = "https://pod.example/alice/ampack/";
        var wac = Turtle.Parse(SolidPacks.ContainerAccess(AccessModels.Wac, container, WebIdOfAlice), container + ".acl");
        var acp = Turtle.Parse(SolidPacks.ContainerAccess(AccessModels.Acp, container, WebIdOfAlice), container + ".acr");

        wac.Should().Contain(t => t.Predicate == Vocabulary.Acl + "default" && t.Object.IsIri(container));
        wac.Where(t => t.Predicate == Vocabulary.Acl + "agent").Select(t => t.Object.Value).Should().Equal(WebIdOfAlice);
        wac.Should().NotContain(t => t.Predicate == Vocabulary.Acl + "agentClass");
        acp.Should().Contain(t => t.Predicate == AccessModels.AcpNamespace + "memberAccessControl");
        acp.Where(t => t.Predicate == AccessModels.AcpNamespace + "agent").Select(t => t.Object.Value).Should().Equal(WebIdOfAlice);
    }

    [Fact]
    public void A_catalog_is_read_up_to_its_bound()
    {
        var text = new StringBuilder();
        for (var i = 0; i < SolidPacks.MaximumEntries + 5; i++)
            text.Append($"<https://pod.example/p{i}> a <https://w3id.org/ampack/ns#Pack> ; <http://purl.org/dc/terms/identifier> \"io.example.p{i}\" ; "
                + $"<https://w3id.org/ampack/ns#version> \"1.0.0\" ; <https://w3id.org/ampack/ns#digest> \"d\" ; <https://w3id.org/ampack/ns#receipt> <https://pod.example/r{i}> .\n");

        Catalog.Read(Turtle.Parse(text.ToString(), "https://pod.example/c.ttl")).Should().HaveCount(SolidPacks.MaximumEntries);
    }

    [Theory]
    [InlineData("BASE <https://other.example/>\n<x> <y> <z> .", "https://other.example/x")]               // SPARQL BASE
    [InlineData("@prefix ex: <https://e.example/> .\nex:a\\. <y> <z> .", "https://e.example/a.")]          // an escaped final dot
    [InlineData("<x> <y> \"v\" @en .", "https://pod.example/x")]                                         // white space before a tag
    [InlineData("<x> <y> \"v\"  ^^<https://e.example/t> .", "https://pod.example/x")]                    // and before a datatype
    public void The_reader_accepts_what_Turtle_allows_that_it_refused_before(string text, string subject) =>
        Turtle.Parse(text, "https://pod.example/doc").Should().Contain(t => t.Subject.Value == subject);

    [Fact]
    public void An_escape_beyond_Unicode_is_refused_not_a_crash() =>
        FluentActions.Invoking(() => Turtle.Parse("<x> <y> \"\\UFFFFFFFF\" .", "https://pod.example/doc")).Should().Throw<InvalidDataException>().WithMessage("*malformed*");
}
