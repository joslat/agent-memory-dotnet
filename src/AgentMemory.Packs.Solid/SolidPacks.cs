using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Packs.Format;

namespace AgentMemory.Packs.Solid;

/// <summary>One pack a catalog lists: discovery hints only; the archive and its receipt are what is trusted.</summary>
internal sealed record CatalogEntry(string Id, string Version, string Digest, string Archive, string Receipt, string? Title);

/// <summary>
/// A publication: where the archive and its receipt now are, which access model enforced it (<c>wac</c> or <c>acp</c>), who
/// the probe found refused and admitted, and the catalogs.
/// </summary>
internal sealed record Publication(string Archive, string Receipt, byte[] ReceiptBytes, string AccessModel, IReadOnlyList<string> Denied,
    IReadOnlyList<string> Allowed, IReadOnlyList<string> Catalogs, string Container);

/// <summary>
/// The two access-control models Solid servers implement (federated packs §7): Web Access Control (an <c>.acl</c> document
/// per resource) and Access Control Policies (an access control resource, typed <c>acp:AccessControlResource</c>). Both are
/// found the same way, through the resource's <c>Link rel="acl"</c>; the type of what it points at decides which one to write.
/// </summary>
internal static class AccessModels
{
    internal const string Wac = "wac";
    internal const string Acp = "acp";
    internal const string AcpNamespace = "http://www.w3.org/ns/solid/acp#";
}

/// <summary>A fetched pack: verified in full, and its placement verified by its receipt.</summary>
internal sealed record FetchedPack(VerifiedPack Pack, byte[] Archive, PublicationReceipt Receipt, byte[] ReceiptBytes, string Location);

/// <summary>
/// Federated packs on Solid Pods (federated packs §5–§7): bind a signing key in the publisher's WebID profile, publish a pack
/// with access rules, a probe and a signed receipt, list it in per-reader catalogs, register the catalog type once; and, on
/// the recipient's side, discover from the publisher's WebID alone, fetch, re-check the key in the publisher's profile (deny
/// only) and verify pack and receipt. Access to a Pod is never trust: the result still goes through the host's install path.
/// </summary>
internal static class SolidPacks
{
    internal const string ArchiveMediaType = "application/vnd.agentmemory.ampack+zip";
    internal const string ReceiptMediaType = "application/vnd.in-toto+json";

    // ---- The publisher's side ----

    /// <summary>Asserts <paramref name="publicKeyPem"/> in the session's own WebID profile (an N3 Patch), unless it already is.</summary>
    internal static async Task<string> BindKeyAsync(SolidSession owner, string publicKeyPem, CancellationToken cancellationToken = default)
    {
        var webId = owner.WebId ?? throw new InvalidOperationException("Binding a key needs a logged-in session.");
        var keyId = PackKey.KeyIdOf(publicKeyPem);
        var profile = await owner.ProfileAsync(webId, cancellationToken).ConfigureAwait(false);
        if (profile.Keys.FirstOrDefault(k => k.KeyId == keyId) is { } known) return known.Method;
        var (method, triples) = WebIdProfile.AssertionFor(webId, publicKeyPem);
        await PatchAsync(owner, Document(webId), triples, cancellationToken).ConfigureAwait(false);
        return method;
    }

    /// <summary>
    /// Publishes a federated pack in the session's own Pod: the publisher WebID must be the session's, and the signer's key
    /// must be one the profile asserts. The probes are other sessions: <paramref name="readers"/> must read the exact bytes back,
    /// <paramref name="strangers"/> must be refused, as must anonymous unless the pack is public; anything else withdraws it.
    /// </summary>
    internal static async Task<Publication> PublishAsync(SolidSession owner, byte[] archive, PackSigner signer, IReadOnlyList<SolidSession> readers,
        IReadOnlyList<SolidSession> strangers, IReadOnlyList<string>? audience = null, CancellationToken cancellationToken = default)
    {
        var webId = owner.WebId ?? throw new InvalidOperationException("Publishing needs a logged-in session.");
        var content = PackReader.ReadArchive(archive, "publication");
        var (integrity, list, listBytes, manifest) = PackVerifier.CheckIntegrity(content);
        if (integrity.Count > 0) throw new InvalidDataException("The pack is not whole: " + string.Join(" ", integrity));
        var federation = manifest!.Federation ?? throw new InvalidDataException("The pack is not federated: it names no publisher or audience.");
        if (federation.Publisher != webId) throw new InvalidDataException($"The pack names publisher {federation.Publisher}; this session is {webId}.");
        var placed = audience ?? federation.Audience;
        if (!federation.IsPublic && (placed is null || placed.Any(a => !federation.Audience!.Contains(a))))
            throw new InvalidDataException("The placement's audience is wider than the pack's: a placement can only narrow it.");

        // The pack must verify against exactly the keys this profile asserts: what a recipient who enrolled from it will hold.
        var profile = await owner.ProfileAsync(webId, cancellationToken).ConfigureAwait(false);
        if (profile.Keys.All(k => k.KeyId != signer.KeyId))
            throw new InvalidDataException($"Key {Dsse.Short(signer.KeyId)} is not asserted in {webId}'s profile; bind it first (ampack solid bind-key).");
        var asserted = new TrustPolicy
        {
            Keys = [.. profile.Keys.Select(k => new TrustedKey(k.KeyId, k.PublicKeyPem, [list!.Id], ["publisher"], null, null, webId))],
            HostWebIds = federation.IsPublic ? [] : [federation.Audience![0]],
        };
        var self = PackVerifier.Verify(content, asserted, DateTimeOffset.UtcNow);
        if (!self.IsVerified) throw new InvalidDataException("The pack does not verify against the keys the profile asserts: " + string.Join(" ", self.Problems));

        var storage = await StorageAsync(owner, profile, cancellationToken).ConfigureAwait(false);
        var container = storage + "ampack/";
        var digest = self.Pack!.Digest;
        var archiveUrl = container + PublicationReceipt.ArchiveNameFor(digest);
        var receiptUrl = container + PublicationReceipt.FileNameFor(digest);

        await PutAsync(owner, archiveUrl, archive, ArchiveMediaType, cancellationToken).ConfigureAwait(false);
        var model = await RestrictAsync(owner, archiveUrl, webId, placed, cancellationToken).ConfigureAwait(false);
        var (denied, allowed) = await ProbeAsync(owner, archiveUrl, archive, placed, readers, strangers, cancellationToken).ConfigureAwait(false);

        // The receipt names how the store enforces the placement: public, or the access model that held under the probe.
        var receipt = new PublicationReceipt(list!.Id, list.Version, digest, archiveUrl, FilesList.Hex(archive), placed, placed is null ? "public" : model,
            denied, allowed, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var receiptBytes = receipt.Sign(signer);
        await PutAsync(owner, receiptUrl, receiptBytes, ReceiptMediaType, cancellationToken).ConfigureAwait(false);
        await RestrictAsync(owner, receiptUrl, webId, placed, cancellationToken).ConfigureAwait(false);

        var entry = new CatalogEntry(list.Id, list.Version, digest, archiveUrl, receiptUrl, Title(content));
        var catalogs = new List<string>();
        foreach (var reader in placed ?? [null!])
        {
            var catalogUrl = reader is null ? container + "public.ttl" : container + "for/" + ReaderKey(reader) + ".ttl";
            var existing = await owner.TurtleAsync(catalogUrl, cancellationToken).ConfigureAwait(false);
            var entries = (existing is null ? [] : Catalog.Read(existing)).Where(e => e.Digest != digest && !(e.Id == entry.Id && e.Version == entry.Version)).Append(entry).ToList();
            await PutAsync(owner, catalogUrl, Encoding.UTF8.GetBytes(Catalog.Write(catalogUrl, webId, entries)), "text/turtle", cancellationToken).ConfigureAwait(false);
            await RestrictAsync(owner, catalogUrl, webId, reader is null ? null : [reader], cancellationToken).ConfigureAwait(false);
            catalogs.Add(catalogUrl);
        }
        await RegisterAsync(owner, profile, storage, container, cancellationToken).ConfigureAwait(false);
        return new Publication(archiveUrl, receiptUrl, receiptBytes, model, denied, allowed, catalogs, container);
    }

    // The probe (federated packs §5, "checked"): what the access rules actually do, tried with real identities.
    private static async Task<(List<string> Denied, List<string> Allowed)> ProbeAsync(SolidSession owner, string url, byte[] archive, IReadOnlyList<string>? audience,
        IReadOnlyList<SolidSession> readers, IReadOnlyList<SolidSession> strangers, CancellationToken cancellationToken)
    {
        var denied = new List<string>();
        var allowed = new List<string>();
        try
        {
            using (var anonymous = SolidSession.Anonymous(owner.Options))
            {
                var response = await anonymous.SendAsync(HttpMethod.Get, url, limit: archive.LongLength, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (audience is null && !(response.IsSuccess && response.Body.AsSpan().SequenceEqual(archive)))
                    throw new InvalidDataException($"The public placement is not readable anonymously ({(int)response.Status}).");
                if (audience is not null)
                {
                    if (response.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                        throw new InvalidDataException($"Anonymous read the placement ({(int)response.Status}): the access rules do not hold.");
                    denied.Add(PublicationReceipt.Anonymous);
                }
            }
            foreach (var reader in readers)
            {
                var who = reader.WebId ?? throw new ArgumentException("A reader probe is a logged-in session.", nameof(readers));
                if (audience is not null && !audience.Contains(who)) throw new InvalidDataException($"{who} probes as a reader but is not in the audience.");
                var response = await reader.SendAsync(HttpMethod.Get, url, limit: archive.LongLength, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess || !response.Body.AsSpan().SequenceEqual(archive))
                    throw new InvalidDataException($"{who} could not read the exact bytes back ({(int)response.Status}).");
                allowed.Add(who);
            }
            foreach (var stranger in strangers)
            {
                var who = stranger.WebId ?? throw new ArgumentException("A stranger probe is a logged-in session.", nameof(strangers));
                if (audience is null || audience.Contains(who)) throw new InvalidDataException($"{who} probes as a stranger but the placement admits it.");
                var response = await stranger.SendAsync(HttpMethod.Get, url, limit: archive.LongLength, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (response.Status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    throw new InvalidDataException($"{who} read the placement ({(int)response.Status}): the access rules do not hold.");
                denied.Add(who);
            }
        }
        catch
        {
            // A placement whose access does not hold is withdrawn, never left behind.
            await owner.SendAsync(HttpMethod.Delete, url, cancellationToken: cancellationToken).ConfigureAwait(false);
            throw;
        }
        return (denied, allowed);
    }

    /// <summary>The access rules (WAC) of one resource: the owner in full, the audience (or everyone, when public) to read.</summary>
    internal static string AccessDocument(string resource, string owner, IReadOnlyList<string>? readers)
    {
        var acl = Vocabulary.Acl;
        var text = new StringBuilder();
        text.Append("<#owner> a <").Append(acl).Append("Authorization> ; <").Append(acl).Append("agent> <").Append(TurtleText.Iri(owner)).Append("> ; <")
            .Append(acl).Append("accessTo> <").Append(TurtleText.Iri(resource)).Append("> ; <").Append(acl).Append("mode> <").Append(acl).Append("Read>, <")
            .Append(acl).Append("Write>, <").Append(acl).Append("Control> .\n");
        text.Append("<#readers> a <").Append(acl).Append("Authorization> ; ");
        if (readers is null) text.Append('<').Append(acl).Append("agentClass> <").Append(Vocabulary.Foaf).Append("Agent> ; ");
        else text.Append('<').Append(acl).Append("agent> ").Append(string.Join(", ", readers.Select(r => "<" + TurtleText.Iri(r) + ">"))).Append(" ; ");
        text.Append('<').Append(acl).Append("accessTo> <").Append(TurtleText.Iri(resource)).Append("> ; <").Append(acl).Append("mode> <").Append(acl).Append("Read> .\n");
        return text.ToString();
    }

    /// <summary>
    /// Writes the access rules of one resource in the model its server uses, and returns that model (<c>wac</c> or <c>acp</c>).
    /// The rules name the owner in full and the readers (or everyone) to read; the probe then checks what they actually do.
    /// </summary>
    internal static async Task<string> RestrictAsync(SolidSession owner, string resource, string ownerWebId, IReadOnlyList<string>? readers, CancellationToken cancellationToken = default)
    {
        var head = await owner.SendAsync(HttpMethod.Head, resource, cancellationToken: cancellationToken).ConfigureAwait(false);
        var acl = head.Links("acl").FirstOrDefault() ?? throw new InvalidDataException($"{resource} names no access-control resource (Link rel=\"acl\"); WAC and ACP are the models supported.");
        if (SolidSession.Origin(acl) != SolidSession.Origin(resource)) throw new InvalidDataException("The access-control resource is on another origin.");
        var model = await ModelAsync(owner, acl, cancellationToken).ConfigureAwait(false);
        var document = model == AccessModels.Acp ? AccessControlResource(resource, ownerWebId, readers) : AccessDocument(resource, ownerWebId, readers);
        await PutAsync(owner, acl, Encoding.UTF8.GetBytes(document), "text/turtle", cancellationToken).ConfigureAwait(false);
        return model;
    }

    // An ACP server types its access control resources acp:AccessControlResource (on every response, even a refusal);
    // a WAC server's .acl document carries no such type.
    private static async Task<string> ModelAsync(SolidSession owner, string acl, CancellationToken cancellationToken)
    {
        var head = await owner.SendAsync(HttpMethod.Head, acl, cancellationToken: cancellationToken).ConfigureAwait(false);
        return head.Links("type").Contains(AccessModels.AcpNamespace + "AccessControlResource") ? AccessModels.Acp : AccessModels.Wac;
    }

    /// <summary>
    /// The access rules (ACP) of one resource: an access control resource whose controls apply a policy allowing the owner
    /// read, write and control, and one allowing the readers (or the public agent) to read.
    /// </summary>
    internal static string AccessControlResource(string resource, string owner, IReadOnlyList<string>? readers)
    {
        const string acp = AccessModels.AcpNamespace;
        var acl = Vocabulary.Acl;
        var who = readers is null ? $"<{acp}PublicAgent>" : string.Join(", ", readers.Select(r => "<" + TurtleText.Iri(r) + ">"));
        return $"<#root> a <{acp}AccessControlResource> ; <{acp}resource> <{TurtleText.Iri(resource)}> ; <{acp}accessControl> <#owner>, <#readers> .\n"
            + $"<#owner> a <{acp}AccessControl> ; <{acp}apply> [ a <{acp}Policy> ; <{acp}allow> <{acl}Read>, <{acl}Write>, <{acl}Control> ; "
            + $"<{acp}anyOf> [ a <{acp}Matcher> ; <{acp}agent> <{TurtleText.Iri(owner)}> ] ] .\n"
            + $"<#readers> a <{acp}AccessControl> ; <{acp}apply> [ a <{acp}Policy> ; <{acp}allow> <{acl}Read> ; "
            + $"<{acp}anyOf> [ a <{acp}Matcher> ; <{acp}agent> {who} ] ] .\n";
    }

    private static async Task RegisterAsync(SolidSession owner, WebIdProfile profile, string storage, string container, CancellationToken cancellationToken)
    {
        var webId = profile.WebId;
        var index = profile.PublicTypeIndex;
        if (index is null)
        {
            index = storage + "settings/publicTypeIndex.ttl";
            var created = $"<{TurtleText.Iri(index)}> a <{Vocabulary.Solid}TypeIndex>, <{Vocabulary.Solid}ListedDocument> .\n";
            await PutAsync(owner, index, Encoding.UTF8.GetBytes(created), "text/turtle", cancellationToken).ConfigureAwait(false);
            await RestrictAsync(owner, index, webId, null, cancellationToken).ConfigureAwait(false);
            await PatchAsync(owner, Document(webId), $"<{TurtleText.Iri(webId)}> <{Vocabulary.Solid}publicTypeIndex> <{TurtleText.Iri(index)}> .\n", cancellationToken).ConfigureAwait(false);
        }
        var triples = await owner.TurtleAsync(index, cancellationToken).ConfigureAwait(false) ?? [];
        if (Containers(triples).Contains(container)) return;
        var registration = Document(index) + "#ampack-catalog";
        await PatchAsync(owner, Document(index), $"<{TurtleText.Iri(registration)}> a <{Vocabulary.Solid}TypeRegistration> ; <{Vocabulary.Solid}forClass> <{Vocabulary.Ampack}Catalog> ; "
            + $"<{Vocabulary.Solid}instanceContainer> <{TurtleText.Iri(container)}> .\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pack containers a type index registers for the catalog class.</summary>
    internal static IReadOnlyList<string> Containers(IReadOnlyList<RdfTriple> index)
    {
        var registrations = index.Where(t => t.Predicate == Vocabulary.Solid + "forClass" && t.Object.IsIri(Vocabulary.Ampack + "Catalog")).Select(t => t.Subject).ToHashSet();
        return [.. index.Where(t => registrations.Contains(t.Subject) && t.Predicate == Vocabulary.Solid + "instanceContainer" && t.Object.Kind == RdfTermKind.Iri)
            .Select(t => t.Object.Value).Distinct()];
    }

    // The storage: named in the profile, or the nearest container above the profile document that says it is one.
    private static async Task<string> StorageAsync(SolidSession session, WebIdProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Storages.Count > 0) return profile.Storages[0].EndsWith('/') ? profile.Storages[0] : profile.Storages[0] + "/";
        var document = Document(profile.WebId);
        var path = document;
        while (true)
        {
            var slash = path.TrimEnd('/').LastIndexOf('/');
            if (slash < path.IndexOf("://", StringComparison.Ordinal) + 3) break;
            path = path[..(slash + 1)];
            var head = await session.SendAsync(HttpMethod.Head, path, anonymous: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (head.Links("type").Contains(Vocabulary.Pim + "Storage")) return path;
        }
        throw new InvalidDataException($"No storage found for {profile.WebId}: its profile names no pim:storage and no container above it is one.");
    }

    private static async Task PutAsync(SolidSession owner, string url, byte[] body, string contentType, CancellationToken cancellationToken)
    {
        var response = await owner.SendAsync(HttpMethod.Put, url, body, contentType, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidDataException($"Writing {url} gave {(int)response.Status}.");
    }

    private static async Task PatchAsync(SolidSession owner, string document, string inserts, CancellationToken cancellationToken)
    {
        var patch = $"@prefix solid: <{Vocabulary.Solid}> .\n_:patch a solid:InsertDeletePatch ; solid:inserts {{\n{inserts}}} .\n";
        var response = await owner.SendAsync(HttpMethod.Patch, document, Encoding.UTF8.GetBytes(patch), "text/n3", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) throw new InvalidDataException($"Patching {document} gave {(int)response.Status}.");
    }

    // ---- The recipient's side ----

    /// <summary>
    /// What <paramref name="publisherWebId"/> offers this session: from its profile's public type index to its pack container,
    /// then this reader's own catalog and the public one. Entries pointing outside the container are dropped.
    /// </summary>
    internal static async Task<IReadOnlyList<CatalogEntry>> DiscoverAsync(SolidSession reader, string publisherWebId, CancellationToken cancellationToken = default)
    {
        var profile = await reader.ProfileAsync(publisherWebId, cancellationToken).ConfigureAwait(false);
        if (profile.PublicTypeIndex is null) return [];
        using var anonymous = SolidSession.Anonymous(reader.Options);
        var index = await anonymous.TurtleAsync(profile.PublicTypeIndex, cancellationToken).ConfigureAwait(false) ?? [];
        var found = new List<CatalogEntry>();
        foreach (var container in Containers(index))
        {
            reader.CheckUrl(container);
            var urls = new List<string> { container + "public.ttl" };
            if (reader.WebId is { } me) urls.Insert(0, container + "for/" + ReaderKey(me) + ".ttl");
            foreach (var url in urls)
                if (await reader.TurtleAsync(url, cancellationToken).ConfigureAwait(false) is { } triples)
                    found.AddRange(Catalog.Read(triples).Where(e => e.Archive.StartsWith(container, StringComparison.Ordinal) && e.Receipt.StartsWith(container, StringComparison.Ordinal)));
        }
        return found.DistinctBy(e => e.Digest).ToList();
    }

    /// <summary>
    /// Fetches one catalog entry and verifies it as this host: the pack in full under <paramref name="policy"/>; its counted
    /// publisher keys still asserted in the publisher's profile (deny only, when <paramref name="recheck"/>); the receipt for this
    /// URL and these bytes. Nothing is installed here.
    /// </summary>
    internal static async Task<FetchedPack> FetchAsync(SolidSession reader, CatalogEntry entry, TrustPolicy policy, bool recheck = true,
        DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var archiveResponse = await reader.SendAsync(HttpMethod.Get, entry.Archive, limit: reader.Options.MaximumArchiveBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!archiveResponse.IsSuccess) throw new InvalidDataException($"{entry.Archive} gave {(int)archiveResponse.Status}.");
        var receiptResponse = await reader.SendAsync(HttpMethod.Get, entry.Receipt, limit: Dsse.MaximumEnvelopeBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!receiptResponse.IsSuccess) throw new InvalidDataException($"{entry.Receipt} gave {(int)receiptResponse.Status}: a pack from a Pod comes with its receipt.");
        var archive = archiveResponse.Body;
        var verified = PackVerifier.Verify(PackReader.ReadArchive(archive, entry.Archive), policy, at);
        if (!verified.IsVerified) throw new InvalidDataException("The pack does not verify: " + string.Join(" ", verified.Problems));
        var pack = verified.Pack!;
        if (pack.Manifest.Federation is not { } federation) throw new InvalidDataException("A pack fetched from a Pod must be federated.");
        if (recheck)
        {
            // The profile can only take trust away: a key its owner no longer asserts refuses the fetch.
            var profile = await reader.ProfileAsync(federation.Publisher, cancellationToken).ConfigureAwait(false);
            var withdrawn = pack.Signatures.Where(s => s.Role == "publisher" && profile.Keys.All(k => k.KeyId != s.KeyId)).Select(s => Dsse.Short(s.KeyId)).ToList();
            if (withdrawn.Count > 0) throw new InvalidDataException($"{federation.Publisher} no longer asserts key {string.Join(", ", withdrawn)} that signed the pack; refused.");
        }
        var (receipt, problems) = PublicationReceipt.Verify(receiptResponse.Body, pack, archive, archiveResponse.Url, policy, at);
        if (receipt is null) throw new InvalidDataException("The receipt does not hold: " + string.Join(" ", problems));
        return new FetchedPack(pack, archive, receipt, receiptResponse.Body, archiveResponse.Url);
    }

    /// <summary>The name of a reader's own catalog: the first 32 hexadecimal characters of the SHA-256 of its WebID.</summary>
    internal static string ReaderKey(string webId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(webId)))[..32].ToLowerInvariant();

    internal static string Document(string iri) => iri.Contains('#', StringComparison.Ordinal) ? iri[..iri.IndexOf('#', StringComparison.Ordinal)] : iri;

    private static string? Title(PackContent content)
    {
        if (!content.Files.TryGetValue(PackManifest.FileName, out var bytes)) return null;
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.TryGetProperty("display", out var display) && display.ValueKind == JsonValueKind.Object
            && display.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null;
    }
}

/// <summary>A pack catalog in Turtle (federated packs §6): written by the publisher's tool, read as hints, never trusted.</summary>
internal static class Catalog
{
    internal static string Write(string catalogUrl, string publisher, IReadOnlyList<CatalogEntry> entries)
    {
        var a = Vocabulary.Ampack;
        var text = new StringBuilder();
        text.Append('<').Append(TurtleText.Iri(catalogUrl)).Append("> a <").Append(a).Append("Catalog> ; <").Append(Vocabulary.Dct).Append("publisher> <")
            .Append(TurtleText.Iri(publisher)).Append("> .\n");
        foreach (var e in entries.OrderBy(e => e.Id, StringComparer.Ordinal).ThenBy(e => e.Version, StringComparer.Ordinal))
        {
            text.Append('<').Append(TurtleText.Iri(catalogUrl)).Append("> <").Append(a).Append("pack> <").Append(TurtleText.Iri(e.Archive)).Append("> .\n");
            text.Append('<').Append(TurtleText.Iri(e.Archive)).Append("> a <").Append(a).Append("Pack> ;\n")
                .Append("    <").Append(Vocabulary.Dct).Append("identifier> \"").Append(TurtleText.Escape(e.Id)).Append("\" ;\n")
                .Append("    <").Append(a).Append("version> \"").Append(TurtleText.Escape(e.Version)).Append("\" ;\n")
                .Append("    <").Append(a).Append("digest> \"").Append(TurtleText.Escape(e.Digest)).Append("\" ;\n")
                .Append("    <").Append(a).Append("receipt> <").Append(TurtleText.Iri(e.Receipt)).Append("> ;\n")
                .Append("    <").Append(Vocabulary.Prov).Append("wasAttributedTo> <").Append(TurtleText.Iri(publisher)).Append('>');
            if (e.Title is { } title) text.Append(" ;\n    <").Append(Vocabulary.Dct).Append("title> \"").Append(TurtleText.Escape(title)).Append('"');
            text.Append(" .\n");
        }
        return text.ToString();
    }

    internal static IReadOnlyList<CatalogEntry> Read(IReadOnlyList<RdfTriple> triples)
    {
        string? Value(RdfTerm subject, string predicate) => triples.FirstOrDefault(t => t.Subject == subject && t.Predicate == predicate)?.Object.Value;
        var entries = new List<CatalogEntry>();
        foreach (var pack in triples.Where(t => t.Predicate == Turtle.Rdf + "type" && t.Object.IsIri(Vocabulary.Ampack + "Pack") && t.Subject.Kind == RdfTermKind.Iri).Select(t => t.Subject).Distinct())
        {
            var id = Value(pack, Vocabulary.Dct + "identifier");
            var version = Value(pack, Vocabulary.Ampack + "version");
            var digest = Value(pack, Vocabulary.Ampack + "digest");
            var receipt = triples.FirstOrDefault(t => t.Subject == pack && t.Predicate == Vocabulary.Ampack + "receipt" && t.Object.Kind == RdfTermKind.Iri)?.Object.Value;
            if (id is null || version is null || digest is null || receipt is null) continue;
            entries.Add(new CatalogEntry(id, version, digest, pack.Value, receipt, Value(pack, Vocabulary.Dct + "title")));
        }
        return entries;
    }
}
