using AgentMemory.Packs.Format;
using AgentMemory.Packs.Solid;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.PackFormat;

/// <summary>
/// The Solid adapter's pure parts (federated packs §6–§7): a strict Turtle reader that reads what servers write and refuses
/// what it cannot read, RFC 3986 resolution that never normalises an identity, WebID profiles and their asserted keys, the
/// access documents and catalogs it writes.
/// </summary>
public sealed class SolidAdapterTests
{
    private const string Base = "https://pod.example/alice/profile/card";
    private const string Me = Base + "#me";

    private static IReadOnlyList<RdfTriple> Parse(string text, string baseIri = Base) => Turtle.Parse(text, baseIri);

    [Fact]
    public void A_profile_as_a_Solid_server_writes_it_is_read()
    {
        var triples = Parse("""
            @prefix foaf: <http://xmlns.com/foaf/0.1/>.
            @prefix solid: <http://www.w3.org/ns/solid/terms#>.

            <>
                a foaf:PersonalProfileDocument;
                foaf:maker <https://pod.example/alice/profile/card#me>;
                foaf:primaryTopic <https://pod.example/alice/profile/card#me>.

            <https://pod.example/alice/profile/card#me>

                solid:oidcIssuer <https://pod.example/>;

                a foaf:Person.
            """);

        triples.Should().Contain(new RdfTriple(RdfTerm.Iri(Base), Turtle.Rdf + "type", RdfTerm.Iri("http://xmlns.com/foaf/0.1/PersonalProfileDocument")));
        triples.Should().Contain(new RdfTriple(RdfTerm.Iri(Me), "http://www.w3.org/ns/solid/terms#oidcIssuer", RdfTerm.Iri("https://pod.example/")));
        triples.Should().HaveCount(5);
    }

    [Fact]
    public void Every_form_of_the_grammar_is_read()
    {
        var triples = Parse(""""
            PREFIX ex: <http://example.org/ns#>
            @base <https://pod.example/data/> .
            # a comment, and a statement with every kind of object
            <#s> ex:p "plain", 'single', """long "quoted" text""", '''long ''single''''', "tab\tnewline\nuni\u00e9"@en-GB,
                     "5"^^<http://www.w3.org/2001/XMLSchema#integer>, 42, -3.5, 1e3, true, false, ex:o, <relative>, _:b1, [ ex:q "inner" ], ( 1 2 ) ;
                 ex:local\.dot ex:with%41percent .
            [] ex:r ex:o .
            """");

        string Obj(int i) => triples.Where(t => t.Predicate == "http://example.org/ns#p").ElementAt(i).Object.Value;
        Obj(0).Should().Be("plain");
        Obj(1).Should().Be("single");
        Obj(2).Should().Be("long \"quoted\" text");
        Obj(3).Should().Be("long ''single''");
        triples.Single(t => t.Object.Language == "en-gb").Object.Value.Should().Be("tab\tnewline\nuni\u00e9");
        triples.Should().Contain(t => t.Object.Value == "42" && t.Object.Datatype == Turtle.Xsd + "integer");
        triples.Should().Contain(t => t.Object.Value == "-3.5" && t.Object.Datatype == Turtle.Xsd + "decimal");
        triples.Should().Contain(t => t.Object.Value == "1e3" && t.Object.Datatype == Turtle.Xsd + "double");
        triples.Should().Contain(t => t.Object.Value == "true" && t.Object.Datatype == Turtle.Xsd + "boolean");
        triples.Should().Contain(t => t.Object.IsIri("https://pod.example/data/relative"));
        triples.Should().Contain(t => t.Subject.IsIri("https://pod.example/data/#s"));
        triples.Should().Contain(t => t.Predicate == "http://example.org/ns#local.dot" && t.Object.IsIri("http://example.org/ns#with%41percent"));
        triples.Should().Contain(t => t.Predicate == Turtle.Rdf + "first" && t.Object.Value == "1");
        triples.Should().Contain(t => t.Predicate == "http://example.org/ns#q" && t.Object.Value == "inner");
        triples.Should().Contain(t => t.Predicate == "http://example.org/ns#r" && t.Subject.Kind == RdfTermKind.Blank);
    }

    [Theory]
    [InlineData("<a> <b> <c>", "'.'")]                                         // no final dot
    [InlineData("<a> <b> \"open .", "unterminated string")]
    [InlineData("<a> <b> \"line\nbreak\" .", "line break")]
    [InlineData("ex:a <b> <c> .", "undeclared prefix")]
    [InlineData("<a b> <c> <d> .", "may not hold")]                            // a space in an IRI
    [InlineData("<a> <b> \"\\q\" .", "unknown escape")]
    [InlineData("<a> <b> \"\\uD800\" .", "malformed")]                          // a lone surrogate
    [InlineData("@foo <x> .", "unknown directive")]
    [InlineData("<a> <b> 1e .", "exponent")]
    [InlineData("<a> <b> ( <c> .", "unclosed")]
    public void What_is_not_Turtle_is_refused_with_its_line(string text, string why)
    {
        var act = () => Parse(text);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{why}*line*");
    }

    [Fact]
    public void Nesting_is_bounded()
    {
        var deep = "<a> <b> " + string.Concat(Enumerable.Repeat("[ <p> ", 70)) + "<o>" + string.Concat(Enumerable.Repeat(" ]", 70)) + " .";
        var act = () => Parse(deep);

        act.Should().Throw<InvalidDataException>().WithMessage("*nesting*");
    }

    [Theory]
    [InlineData("https://a.example/b/c/d;p?q", "g", "https://a.example/b/c/g")]
    [InlineData("https://a.example/b/c/d;p?q", "./g", "https://a.example/b/c/g")]
    [InlineData("https://a.example/b/c/d;p?q", "g/", "https://a.example/b/c/g/")]
    [InlineData("https://a.example/b/c/d;p?q", "/g", "https://a.example/g")]
    [InlineData("https://a.example/b/c/d;p?q", "//g", "https://g")]
    [InlineData("https://a.example/b/c/d;p?q", "?y", "https://a.example/b/c/d;p?y")]
    [InlineData("https://a.example/b/c/d;p?q", "#s", "https://a.example/b/c/d;p?q#s")]
    [InlineData("https://a.example/b/c/d;p?q", "../g", "https://a.example/b/g")]
    [InlineData("https://a.example/b/c/d;p?q", "../../../g", "https://a.example/g")]
    [InlineData("https://a.example/b/c/d;p?q", "", "https://a.example/b/c/d;p?q")]
    [InlineData("https://Pod.Example:443/Card", "#me", "https://Pod.Example:443/Card#me")]   // never normalised
    public void Relative_references_resolve_by_RFC_3986_without_normalising(string baseIri, string reference, string expected) =>
        Iris.Resolve(baseIri, reference).Should().Be(expected);

    [Fact]
    public void A_profile_yields_its_issuer_storage_type_index_and_the_P256_keys_it_asserts_for_itself()
    {
        var key = PackKey.Create();
        var other = PackKey.Create();
        var (method, assertion) = WebIdProfile.AssertionFor(Me, key.PublicKeyPem);
        var (_, foreign) = WebIdProfile.AssertionFor("https://other.example/card#me", other.PublicKeyPem);
        var text = $"""
            @prefix solid: <http://www.w3.org/ns/solid/terms#> .
            @prefix pim: <http://www.w3.org/ns/pim/space#> .
            <#me> solid:oidcIssuer <https://pod.example/> ; pim:storage </alice/> ; solid:publicTypeIndex </alice/settings/publicTypeIndex.ttl> .
            {assertion}
            <#me> <https://w3id.org/security#assertionMethod> <https://other.example/card#ampack-x> .
            {foreign.Replace("https://other.example/card#ampack-" + other.KeyId[..16], "https://other.example/card#ampack-x", StringComparison.Ordinal)}
            """;

        var profile = WebIdProfile.Read(Me, Parse(text));

        profile.Issuers.Should().Equal("https://pod.example/");
        profile.Storages.Should().Equal("https://pod.example/alice/");
        profile.PublicTypeIndex.Should().Be("https://pod.example/alice/settings/publicTypeIndex.ttl");
        profile.Keys.Should().ContainSingle().Which.Should().Be(new AssertedKey(method, key.KeyId, profile.Keys[0].PublicKeyPem));
        PackKey.KeyIdOf(profile.Keys[0].PublicKeyPem).Should().Be(key.KeyId, "the key read back is the key asserted");
    }

    [Theory]
    [InlineData("{\"crv\":\"P-384\",\"kty\":\"EC\",\"x\":\"AA\",\"y\":\"AA\"}")]
    [InlineData("{\"kty\":\"RSA\",\"n\":\"AA\",\"e\":\"AQAB\"}")]
    [InlineData("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"short\",\"y\":\"short\"}")]
    [InlineData("not json")]
    public void Only_a_P256_public_JWK_becomes_a_key(string jwk) => Jwk.ToPem(jwk).Should().BeNull();

    [Fact]
    public void A_JWK_with_a_private_part_is_never_read_as_a_public_key()
    {
        var jwk = Jwk.FromPem(PackKey.Create().PublicKeyPem);

        Jwk.ToPem(jwk).Should().NotBeNull();
        Jwk.ToPem(jwk.Replace("}", ",\"d\":\"secret\"}", StringComparison.Ordinal)).Should().BeNull();
    }

    [Fact]
    public void The_access_document_grants_the_owner_everything_and_the_audience_or_everyone_read()
    {
        const string resource = "https://pod.example/alice/ampack/sha256-ab.ampack";
        var named = Parse(SolidPacks.AccessDocument(resource, Me, ["https://bob.example/card#me", "https://carol.example/card#me"]), resource + ".acl");
        var everyone = Parse(SolidPacks.AccessDocument(resource, Me, null), resource + ".acl");
        const string acl = "http://www.w3.org/ns/auth/acl#";

        named.Where(t => t.Predicate == acl + "agent").Select(t => t.Object.Value).Should().BeEquivalentTo(Me, "https://bob.example/card#me", "https://carol.example/card#me");
        named.Where(t => t.Predicate == acl + "mode" && t.Subject.Value == resource + ".acl#readers").Select(t => t.Object.Value).Should().Equal(acl + "Read");
        named.Where(t => t.Predicate == acl + "mode" && t.Subject.Value == resource + ".acl#owner").Should().HaveCount(3);
        named.Should().NotContain(t => t.Predicate == acl + "agentClass");
        everyone.Should().Contain(t => t.Predicate == acl + "agentClass" && t.Object.IsIri("http://xmlns.com/foaf/0.1/Agent"));
    }

    [Fact]
    public void The_access_control_resource_applies_an_owner_policy_and_a_readers_policy_or_the_public_agent()
    {
        const string resource = "https://pod.example/alice/ampack/sha256-ab.ampack";
        const string acp = "http://www.w3.org/ns/solid/acp#";
        const string acl = "http://www.w3.org/ns/auth/acl#";
        var named = Parse(SolidPacks.AccessControlResource(resource, Me, ["https://bob.example/card#me", "https://carol.example/card#me"]), resource + ".acr");
        var everyone = Parse(SolidPacks.AccessControlResource(resource, Me, null), resource + ".acr");

        named.Should().Contain(new RdfTriple(RdfTerm.Iri(resource + ".acr#root"), acp + "resource", RdfTerm.Iri(resource)));
        named.Where(t => t.Subject.IsIri(resource + ".acr#root") && t.Predicate == acp + "accessControl").Select(t => t.Object.Value)
            .Should().BeEquivalentTo(resource + ".acr#owner", resource + ".acr#readers", resource + ".acr#noOtherWriter", resource + ".acr#noOtherReader");
        named.Where(t => t.Predicate == acp + "allow").Select(t => t.Object.Value).Should().BeEquivalentTo(acl + "Read", acl + "Write", acl + "Control", acl + "Read");
        // ACP adds whatever ancestors allow (a Pod-wide share), and only a deny takes it away: everyone but the owner and the
        // readers is denied reading, everyone but the owner writing (FED-review round 2 M-A).
        SolidSessionTests.Denies(named, resource + ".acr#noOtherReader").Should().BeEquivalentTo(
            (new[] { acl + "Read" }, new[] { Me, "https://bob.example/card#me", "https://carol.example/card#me" }));
        SolidSessionTests.Denies(named, resource + ".acr#noOtherWriter").Should().BeEquivalentTo((new[] { acl + "Write", acl + "Append", acl + "Control" }, new[] { Me }));
        everyone.Should().Contain(t => t.Predicate == acp + "agent" && t.Object.IsIri(acp + "PublicAgent"));
        everyone.Should().NotContain(t => t.Subject.IsIri(resource + ".acr#noOtherReader"), "a public placement denies nobody reading");
        SolidSessionTests.Denies(everyone, resource + ".acr#noOtherWriter").Spared.Should().Equal(Me);
        FluentActions.Invoking(() => SolidPacks.AccessControlResource(resource, "https://evil.example/card> <x", null)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_IRI_that_would_break_Turtle_is_never_written() =>
        FluentActions.Invoking(() => SolidPacks.AccessDocument("https://pod.example/x", "https://evil.example/card> <x", null))
            .Should().Throw<InvalidDataException>();

    [Fact]
    public void A_catalog_round_trips_and_its_literals_are_escaped()
    {
        const string url = "https://pod.example/alice/ampack/for/0011.ttl";
        var entries = new[]
        {
            new CatalogEntry("io.example.notes", "1.0.0", "sha256:" + new string('a', 64), "https://pod.example/alice/ampack/sha256-a.ampack",
                "https://pod.example/alice/ampack/sha256-a.receipt.dsse.json", "Notes \"quoted\"\nand a line"),
            new CatalogEntry("io.example.other", "2.1.0", "sha256:" + new string('b', 64), "https://pod.example/alice/ampack/sha256-b.ampack",
                "https://pod.example/alice/ampack/sha256-b.receipt.dsse.json", null),
        };

        var read = Catalog.Read(Parse(Catalog.Write(url, Me, entries), url));

        read.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public void A_type_index_names_the_pack_containers_registered_for_the_catalog_class()
    {
        var index = Parse("""
            @prefix solid: <http://www.w3.org/ns/solid/terms#> .
            <> a solid:TypeIndex, solid:ListedDocument .
            <#ampack-catalog> a solid:TypeRegistration ; solid:forClass <https://w3id.org/ampack/ns#Catalog> ; solid:instanceContainer </alice/ampack/> .
            <#notes> a solid:TypeRegistration ; solid:forClass <http://example.org/Note> ; solid:instanceContainer </alice/notes/> .
            """, "https://pod.example/alice/settings/publicTypeIndex.ttl");

        SolidPacks.Containers(index).Should().Equal("https://pod.example/alice/ampack/");
    }

    [Fact]
    public void A_readers_catalog_is_named_by_a_hash_of_its_WebId_alone()
    {
        SolidPacks.ReaderKey(Me).Should().HaveLength(32).And.MatchRegex("^[0-9a-f]+$");
        SolidPacks.ReaderKey(Me).Should().NotBe(SolidPacks.ReaderKey(Me.ToUpperInvariant()), "WebIDs are exact strings");
    }

    [Fact]
    public void A_session_refuses_plain_http_off_this_machine_and_user_information()
    {
        using var strict = SolidSession.Anonymous();
        using var local = SolidSession.Anonymous(new SolidOptions { AllowLoopbackHttp = true });

        FluentActions.Invoking(() => strict.CheckUrl("http://localhost:3000/x")).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => local.CheckUrl("http://pod.example/x")).Should().Throw<InvalidDataException>();
        FluentActions.Invoking(() => local.CheckUrl("https://user:pw@pod.example/x")).Should().Throw<InvalidDataException>();
        local.CheckUrl("http://localhost:3000/x");
        strict.CheckUrl("https://pod.example/x");
    }

    [Fact]
    public void Link_headers_are_read_by_relation_and_resolved()
    {
        var response = new SolidResponse(System.Net.HttpStatusCode.OK, "https://pod.example/alice/x",
            new Dictionary<string, IReadOnlyList<string>> { ["Link"] = ["<x.acl>; rel=\"acl\", <http://www.w3.org/ns/ldp#Resource>; rel=\"type\"", "<http://www.w3.org/ns/pim/space#Storage>; rel=\"type\""] },
            []);

        response.Links("acl").Should().Equal("https://pod.example/alice/x.acl");
        response.Links("type").Should().Equal("http://www.w3.org/ns/ldp#Resource", "http://www.w3.org/ns/pim/space#Storage");
    }
}
