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
    private static Scripted Pod(string tokenWebId = WebIdOfAlice, string issuerNamed = Issuer, bool wantNonce = false, string? configuration = null)
    {
        var nonceAsked = false;
        return new Scripted(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url == "https://pod.example/alice/profile/card")
                return Text($"<#me> <http://www.w3.org/ns/solid/terms#oidcIssuer> <{Issuer}> .");
            if (url == "https://pod.example/.well-known/openid-configuration")
                return Text(configuration ?? $$"""{"issuer":"{{issuerNamed}}","token_endpoint":"https://pod.example/.oidc/token"}""", "application/json");
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

    [Theory]
    [InlineData("""{"issuer":42,"token_endpoint":"https://pod.example/.oidc/token"}""", "names another issuer")]
    [InlineData("not json", "not a JSON object")]
    [InlineData("""["https://pod.example/"]""", "not a JSON object")]
    public async Task An_issuer_configuration_that_is_not_one_is_refused_never_a_crash(string configuration, string why) =>
        await FluentActions.Awaiting(() => SolidSession.LoginAsync(WebIdOfAlice, "id", "secret", handler: Pod(configuration: configuration)))
            .Should().ThrowAsync<InvalidDataException>().WithMessage($"*{why}*");

    [Fact]
    public void A_handler_that_follows_redirects_itself_is_refused()
    {
        using var follows = new HttpClientHandler();
        using var stays = new HttpClientHandler { AllowAutoRedirect = false };

        FluentActions.Invoking(() => SolidSession.Anonymous(handler: follows)).Should().Throw<ArgumentException>().WithMessage("*redirects*");
        using var session = SolidSession.Anonymous(handler: stays);
    }

    [Theory]
    [InlineData("https://10.0.0.5/x")]
    [InlineData("https://169.254.169.254/latest/")]
    [InlineData("https://[fd00::1]/x")]
    [InlineData("https://127.0.0.1/x")]                        // loopback too, unless a local server is allowed
    public void An_IP_address_is_never_reached(string url)
    {
        using var strict = SolidSession.Anonymous();

        FluentActions.Invoking(() => strict.CheckUrl(url)).Should().Throw<InvalidDataException>().WithMessage("*IP address*");
    }

    [Fact]
    public void A_local_test_server_may_be_reached_by_its_loopback_address()
    {
        using var local = SolidSession.Anonymous(new SolidOptions { AllowLoopbackHttp = true });

        local.CheckUrl("http://127.0.0.1:3000/x");
        FluentActions.Invoking(() => local.CheckUrl("https://10.0.0.5/x")).Should().Throw<InvalidDataException>();
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
    public async Task A_readers_credentials_reach_a_storage_on_another_origin_only_when_it_names_the_publisher_as_its_owner()
    {
        // Each storage root answers as a private one answers anyone: refused, with the links the Solid Protocol puts there.
        var handler = new Scripted(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (request.RequestUri!.Host == "storage.example")
                response.Headers.Add("Link", $"<{Vocabulary.Pim}Storage>; rel=\"type\", <{WebIdOfAlice}>; rel=\"{Vocabulary.Solid}owner\"");
            if (request.RequestUri!.Host == "squat.example") response.Headers.Add("Link", $"<{Vocabulary.Pim}Storage>; rel=\"type\"");
            return response;
        });
        using var session = SolidSession.Anonymous(handler: handler);
        var profile = new WebIdProfile(WebIdOfAlice, [Issuer], ["https://storage.example/alice/", "https://squat.example/", "https://10.0.0.5/"], null, []);
        var reach = new PublisherReach(session, profile);

        (await reach.AllowsAsync("https://pod.example/alice/ampack/")).Should().BeTrue("the WebID document's own origin");
        (await reach.AllowsAsync("https://storage.example/alice/ampack/")).Should().BeTrue("a storage that names Alice as its owner");
        (await reach.AllowsAsync("https://storage.example/alice/ampack/for/x.ttl")).Should().BeTrue();
        (await reach.AllowsAsync("https://storage.example/bob/ampack/")).Should().BeFalse("outside the storage Alice declared");
        (await reach.AllowsAsync("https://squat.example/ampack/")).Should().BeFalse("a storage that names no owner");
        (await reach.AllowsAsync("https://elsewhere.example/ampack/")).Should().BeFalse("no storage of Alice's is there");
        await FluentActions.Awaiting(() => reach.AllowsAsync("https://10.0.0.5/ampack/")).Should().ThrowAsync<InvalidDataException>().WithMessage("*IP address*");
        handler.Seen.Select(r => r.RequestUri!.Host).Should().Equal(["storage.example", "squat.example"], "each storage is asked once");
        handler.Seen.Should().OnlyContain(r => r.Method == HttpMethod.Head && r.Headers.Authorization == null);
    }

    [Fact]
    public void A_catalog_counts_only_for_the_publisher_it_names()
    {
        const string url = "https://pod.example/alice/ampack/public.ttl";
        var triples = Turtle.Parse(Catalog.Write(url, WebIdOfAlice, []), url);

        Catalog.IsBy(triples, url, WebIdOfAlice).Should().BeTrue();
        Catalog.IsBy(triples, url, "https://pod.example/mallory/profile/card#me").Should().BeFalse("on a server with many Pods, another profile may point here");
        Catalog.IsBy(triples, "https://pod.example/bob/ampack/public.ttl", WebIdOfAlice).Should().BeFalse();
    }

    /// <summary>The modes an ACP control denies, the agents it spares, and that it applies to every agent otherwise.</summary>
    internal static (IReadOnlyList<string> Modes, IReadOnlyList<string> Spared) Denies(IReadOnlyList<RdfTriple> triples, string control)
    {
        const string acp = AccessModels.AcpNamespace;
        var policy = triples.Single(t => t.Subject.IsIri(control) && t.Predicate == acp + "apply").Object;
        var all = triples.Single(t => t.Subject == policy && t.Predicate == acp + "allOf").Object;
        triples.Should().Contain(t => t.Subject == all && t.Predicate == acp + "agent" && t.Object.IsIri(acp + "PublicAgent"));
        var none = triples.Single(t => t.Subject == policy && t.Predicate == acp + "noneOf").Object;
        return ([.. triples.Where(t => t.Subject == policy && t.Predicate == acp + "deny").Select(t => t.Object.Value)],
            [.. triples.Where(t => t.Subject == none && t.Predicate == acp + "agent").Select(t => t.Object.Value)]);
    }

    [Fact]
    public void Container_access_is_owner_only_in_both_models_and_covers_what_has_no_rules_of_its_own()
    {
        const string container = "https://pod.example/alice/ampack/";
        const string acp = AccessModels.AcpNamespace;
        var wac = Turtle.Parse(SolidPacks.ContainerAccess(AccessModels.Wac, container, WebIdOfAlice), container + ".acl");
        var acr = Turtle.Parse(SolidPacks.ContainerAccess(AccessModels.Acp, container, WebIdOfAlice), container + ".acr");
        IEnumerable<string> Controls(string predicate) => acr.Where(t => t.Predicate == acp + predicate).Select(t => t.Object.Value[(container.Length + 5)..]);

        wac.Should().Contain(t => t.Predicate == Vocabulary.Acl + "default" && t.Object.IsIri(container));
        wac.Where(t => t.Predicate == Vocabulary.Acl + "agent").Select(t => t.Object.Value).Should().Equal(WebIdOfAlice);
        wac.Should().NotContain(t => t.Predicate == Vocabulary.Acl + "agentClass");
        // ACP adds what ancestors allow: the listing is denied to everyone but the owner, writing members likewise; reading a
        // member is left to the member's own rules, which deny it to everyone but its readers.
        Controls("accessControl").Should().BeEquivalentTo("owner", "noOtherReader", "noOtherWriter");
        Controls("memberAccessControl").Should().BeEquivalentTo("owner", "noOtherWriter");
        Denies(acr, container + ".acr#noOtherReader").Should().BeEquivalentTo((new[] { Vocabulary.Acl + "Read" }, new[] { WebIdOfAlice }));
        Denies(acr, container + ".acr#noOtherWriter").Modes.Should().BeEquivalentTo(Vocabulary.Acl + "Write", Vocabulary.Acl + "Append", Vocabulary.Acl + "Control");
        Denies(acr, container + ".acr#noOtherWriter").Spared.Should().Equal(WebIdOfAlice);
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
