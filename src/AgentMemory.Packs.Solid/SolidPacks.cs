using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMemory.Packs.Format;

namespace AgentMemory.Packs.Solid;

/// <summary>
/// One pack a catalog lists: discovery hints only; the archive and its receipt are what is trusted. <paramref name="Publisher"/>
/// is the WebID discovery started from, which the fetched pack must name.
/// </summary>
internal sealed record CatalogEntry(string Id, string Version, string Digest, string Archive, string Receipt, string? Title, string? Publisher = null);

/// <summary>
/// A publication: where the archive and its receipt now are, which access model enforced it (<c>wac</c> or <c>acp</c>), who
/// the probe found refused and admitted, and the catalogs.
/// </summary>
internal sealed record Publication(string Archive, string Receipt, byte[] ReceiptBytes, string AccessModel, IReadOnlyList<string> Denied,
    IReadOnlyList<string> Allowed, IReadOnlyList<string> Catalogs, string Container);

/// <summary>A fetched pack: verified in full (a set with every member), and its placement verified by its receipt.</summary>
internal sealed record FetchedPack(VerifiedPack Pack, byte[] Archive, PublicationReceipt Receipt, byte[] ReceiptBytes, string Location, string ReceiptSignedBy);

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

/// <summary>
/// Federated packs on Solid Pods (federated packs §5–§7): bind a signing key in the publisher's WebID profile, publish a pack
/// with access rules, a probe and a signed receipt, list it in per-reader catalogs, register the catalog type once; and, on
/// the recipient's side, discover from the publisher's WebID alone, fetch, verify pack and receipt, and re-check both keys in
/// the publisher's profile (deny only). A reader's credentials only ever reach the publisher's own origins. Access to a Pod is
/// never trust: the result still goes through the host's install path.
/// </summary>
internal static partial class SolidPacks
{
    internal const string ArchiveMediaType = "application/vnd.agentmemory.ampack+zip";
    internal const string ReceiptMediaType = "application/vnd.in-toto+json";
    /// <summary>Most pack containers read from one type index.</summary>
    internal const int MaximumContainers = 8;
    /// <summary>Most entries read from one catalog.</summary>
    internal const int MaximumEntries = 1_000;

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
    /// <paramref name="strangers"/> must be refused, as must anonymous unless the pack is public; the receipt and the catalogs
    /// are probed anonymously too, and the pack containers must refuse anonymous listing. Anything else withdraws what was placed.
    /// </summary>
    internal static async Task<Publication> PublishAsync(SolidSession owner, byte[] archive, PackSigner signer, IReadOnlyList<SolidSession> readers,
        IReadOnlyList<SolidSession> strangers, IReadOnlyList<string>? audience = null, CancellationToken cancellationToken = default)
    {
        var webId = owner.WebId ?? throw new InvalidOperationException("Publishing needs a logged-in session.");
        var content = PackReader.ReadArchive(archive, "publication");
        var (integrity, list, _, manifest) = PackVerifier.CheckIntegrity(content);
        if (integrity.Count > 0) throw new InvalidDataException("The pack is not whole: " + string.Join(" ", integrity));
        var federation = manifest!.Federation ?? throw new InvalidDataException("The pack is not federated: it names no publisher or audience.");
        if (federation.Publisher != webId) throw new InvalidDataException($"The pack names publisher {federation.Publisher}; this session is {webId}.");
        var placed = audience ?? federation.Audience;
        if (placed is not null && (placed.Count is 0 or > PackFederation.MaximumAudience || placed.Any(a => !WebId.IsValid(a)) || placed.Distinct(StringComparer.Ordinal).Count() != placed.Count))
            throw new InvalidDataException("The placement's audience is 1 to 64 distinct WebIDs.");
        if (!federation.IsPublic && (placed is null || placed.Any(a => !federation.Audience!.Contains(a))))
            throw new InvalidDataException("The placement's audience is wider than the pack's: a placement can only narrow it.");

        // The pack must verify, set members included, against exactly the keys this profile asserts: what a recipient who
        // enrolled from it will hold.
        var profile = await owner.ProfileAsync(webId, cancellationToken).ConfigureAwait(false);
        if (profile.Keys.All(k => k.KeyId != signer.KeyId))
            throw new InvalidDataException($"Key {Dsse.Short(signer.KeyId)} is not asserted in {webId}'s profile; bind it first (ampack solid bind-key).");
        var asserted = new TrustPolicy
        {
            Keys = [.. profile.Keys.Select(k => new TrustedKey(k.KeyId, k.PublicKeyPem, [list!.Id], ["publisher"], null, null, webId))],
            HostWebIds = placed is null ? [] : [placed[0]],
        };
        var self = PackVerifier.VerifyWhole(content, asserted, DateTimeOffset.UtcNow);
        if (!self.IsVerified) throw new InvalidDataException("The pack does not verify against the keys the profile asserts: " + string.Join(" ", self.Problems));

        var storage = await StorageAsync(owner, profile, cancellationToken).ConfigureAwait(false);
        var container = storage + "ampack/";
        var digest = self.Pack!.Digest;
        var archiveUrl = container + PublicationReceipt.ArchiveNameFor(digest);
        var receiptUrl = container + PublicationReceipt.FileNameFor(digest);
        var placedNow = new List<string>();
        try
        {
            await PutAsync(owner, archiveUrl, archive, ArchiveMediaType, cancellationToken).ConfigureAwait(false);
            placedNow.Add(archiveUrl);
            await RestrictContainerAsync(owner, container, webId, cancellationToken).ConfigureAwait(false);
            var model = await RestrictAsync(owner, archiveUrl, webId, placed, cancellationToken).ConfigureAwait(false);
            var (denied, allowed) = await ProbeAsync(owner, archiveUrl, archive, placed, readers, strangers, cancellationToken).ConfigureAwait(false);

            // The receipt names how the store enforces the placement: public, or the access model that held under the probe.
            var receipt = new PublicationReceipt(list!.Id, list.Version, digest, archiveUrl, FilesList.Hex(archive), placed, placed is null ? "public" : model,
                denied, allowed, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var receiptBytes = receipt.Sign(signer);
            await PutAsync(owner, receiptUrl, receiptBytes, ReceiptMediaType, cancellationToken).ConfigureAwait(false);
            placedNow.Add(receiptUrl);
            await RestrictAsync(owner, receiptUrl, webId, placed, cancellationToken).ConfigureAwait(false);
            await AnonymousProbeAsync(owner, receiptUrl, placed is null, cancellationToken).ConfigureAwait(false);

            var entry = new CatalogEntry(list.Id, list.Version, digest, archiveUrl, receiptUrl, Title(content));
            var catalogs = new List<string>();
            foreach (var reader in placed ?? [null!])
            {
                var catalogUrl = reader is null ? container + "public.ttl" : container + "for/" + ReaderKey(reader) + ".ttl";
                var existing = await owner.TurtleAsync(catalogUrl, cancellationToken: cancellationToken).ConfigureAwait(false);
                var entries = (existing is null ? [] : Catalog.Read(existing)).Where(e => e.Digest != digest && !(e.Id == entry.Id && e.Version == entry.Version)).Append(entry).ToList();
                await PutAsync(owner, catalogUrl, Encoding.UTF8.GetBytes(Catalog.Write(catalogUrl, webId, entries)), "text/turtle", cancellationToken).ConfigureAwait(false);
                await RestrictAsync(owner, catalogUrl, webId, reader is null ? null : [reader], cancellationToken).ConfigureAwait(false);
                await AnonymousProbeAsync(owner, catalogUrl, reader is null, cancellationToken).ConfigureAwait(false);
                catalogs.Add(catalogUrl);
            }
            if (placed is not null) await RestrictContainerAsync(owner, container + "for/", webId, cancellationToken).ConfigureAwait(false);
            await RegisterAsync(owner, profile, storage, container, cancellationToken).ConfigureAwait(false);
            return new Publication(archiveUrl, receiptUrl, receiptBytes, model, denied, allowed, catalogs, container);
        }
        catch (Exception failure) when (failure is InvalidDataException or HttpRequestException or TaskCanceledException)
        {
            // What this publication placed is withdrawn, and the withdrawal itself is checked: a pack whose access did not hold
            // and that could not be removed is said so, loudly (FED-review M3).
            var stuck = new List<string>();
            foreach (var url in Enumerable.Reverse(placedNow))
            {
                try
                {
                    var removed = await owner.SendAsync(HttpMethod.Delete, url, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    if (!removed.IsSuccess && removed.Status != HttpStatusCode.NotFound) stuck.Add($"{url} ({(int)removed.Status})");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException) { stuck.Add($"{url} ({e.GetType().Name})"); }
            }
            if (stuck.Count > 0)
                throw new InvalidDataException($"{failure.Message} WITHDRAWAL FAILED: remove these by hand, they may still be readable: {string.Join(", ", stuck)}.", failure);
            throw;
        }
    }

    // The probe (federated packs §5, "checked"): what the access rules actually do, tried with real identities.
    private static async Task<(List<string> Denied, List<string> Allowed)> ProbeAsync(SolidSession owner, string url, byte[] archive, IReadOnlyList<string>? audience,
        IReadOnlyList<SolidSession> readers, IReadOnlyList<SolidSession> strangers, CancellationToken cancellationToken)
    {
        var denied = new List<string>();
        var allowed = new List<string>();
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
        return (denied, allowed);
    }

    // Anonymous must be refused a resource that is not public (or a container listing), and must read a public one.
    private static async Task AnonymousProbeAsync(SolidSession owner, string url, bool isPublic, CancellationToken cancellationToken)
    {
        using var anonymous = SolidSession.Anonymous(owner.Options);
        var response = await anonymous.SendAsync(HttpMethod.Get, url, limit: owner.Options.MaximumDocumentBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        var refused = response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        if (isPublic ? !response.IsSuccess : !refused)
            throw new InvalidDataException($"Anonymous {(isPublic ? "cannot read" : "read")} {url} ({(int)response.Status}): the access rules do not hold.");
    }

    /// <summary>The access rules (WAC) of one resource: the owner in full, the audience (or everyone, when public) to read.</summary>
    internal static string AccessDocument(string resource, string owner, IReadOnlyList<string>? readers)
    {
        var acl = Vocabulary.Acl;
        var who = readers is null ? $"<{acl}agentClass> <{Vocabulary.Foaf}Agent>" : $"<{acl}agent> " + string.Join(", ", readers.Select(r => "<" + TurtleText.Iri(r) + ">"));
        return $"<#owner> a <{acl}Authorization> ; <{acl}agent> <{TurtleText.Iri(owner)}> ; <{acl}accessTo> <{TurtleText.Iri(resource)}> ; "
            + $"<{acl}mode> <{acl}Read>, <{acl}Write>, <{acl}Control> .\n"
            + $"<#readers> a <{acl}Authorization> ; {who} ; <{acl}accessTo> <{TurtleText.Iri(resource)}> ; <{acl}mode> <{acl}Read> .\n";
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

    /// <summary>
    /// The owner-only rules of a pack container, in either model, for the container and (as its default) for whatever in it
    /// has no rules of its own: its listing would otherwise expose every digest and every reader's catalog name (FED-review M4).
    /// </summary>
    internal static string ContainerAccess(string model, string container, string owner)
    {
        var acl = Vocabulary.Acl;
        if (model == AccessModels.Acp)
        {
            const string acp = AccessModels.AcpNamespace;
            return $"<#root> a <{acp}AccessControlResource> ; <{acp}resource> <{TurtleText.Iri(container)}> ; <{acp}accessControl> <#owner> ; <{acp}memberAccessControl> <#owner> .\n"
                + $"<#owner> a <{acp}AccessControl> ; <{acp}apply> [ a <{acp}Policy> ; <{acp}allow> <{acl}Read>, <{acl}Write>, <{acl}Control> ; "
                + $"<{acp}anyOf> [ a <{acp}Matcher> ; <{acp}agent> <{TurtleText.Iri(owner)}> ] ] .\n";
        }
        return $"<#owner> a <{acl}Authorization> ; <{acl}agent> <{TurtleText.Iri(owner)}> ; <{acl}accessTo> <{TurtleText.Iri(container)}> ; "
            + $"<{acl}default> <{TurtleText.Iri(container)}> ; <{acl}mode> <{acl}Read>, <{acl}Write>, <{acl}Control> .\n";
    }

    /// <summary>
    /// Writes the access rules of one resource in the model its server uses, and returns that model (<c>wac</c> or <c>acp</c>).
    /// The rules name the owner in full and the readers (or everyone) to read; the probe then checks what they actually do.
    /// </summary>
    internal static async Task<string> RestrictAsync(SolidSession owner, string resource, string ownerWebId, IReadOnlyList<string>? readers, CancellationToken cancellationToken = default)
    {
        var (acl, model) = await AccessResourceAsync(owner, resource, cancellationToken).ConfigureAwait(false);
        var document = model == AccessModels.Acp ? AccessControlResource(resource, ownerWebId, readers) : AccessDocument(resource, ownerWebId, readers);
        await PutAsync(owner, acl, Encoding.UTF8.GetBytes(document), "text/turtle", cancellationToken).ConfigureAwait(false);
        return model;
    }

    private static async Task RestrictContainerAsync(SolidSession owner, string container, string ownerWebId, CancellationToken cancellationToken)
    {
        var (acl, model) = await AccessResourceAsync(owner, container, cancellationToken).ConfigureAwait(false);
        await PutAsync(owner, acl, Encoding.UTF8.GetBytes(ContainerAccess(model, container, ownerWebId)), "text/turtle", cancellationToken).ConfigureAwait(false);
        await AnonymousProbeAsync(owner, container, isPublic: false, cancellationToken).ConfigureAwait(false);
    }

    // The access resource a server names for a resource (Link rel="acl"), on the resource's own origin, and its model: an ACP
    // server types it acp:AccessControlResource on every response, even a refusal; a WAC server's .acl carries no such type.
    private static async Task<(string Acl, string Model)> AccessResourceAsync(SolidSession owner, string resource, CancellationToken cancellationToken)
    {
        var head = await owner.SendAsync(HttpMethod.Head, resource, cancellationToken: cancellationToken).ConfigureAwait(false);
        var acl = head.Links("acl").FirstOrDefault() ?? throw new InvalidDataException($"{resource} names no access-control resource (Link rel=\"acl\"); WAC and ACP are the models supported.");
        if (SolidSession.Origin(acl) != SolidSession.Origin(resource)) throw new InvalidDataException("The access-control resource is on another origin.");
        var typed = await owner.SendAsync(HttpMethod.Head, acl, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (acl, typed.Links("type").Contains(AccessModels.AcpNamespace + "AccessControlResource") ? AccessModels.Acp : AccessModels.Wac);
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
        var triples = await owner.TurtleAsync(index, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        if (Containers(triples).Contains(container)) return;
        var registration = Document(index) + "#ampack-catalog";
        await PatchAsync(owner, Document(index), $"<{TurtleText.Iri(registration)}> a <{Vocabulary.Solid}TypeRegistration> ; <{Vocabulary.Solid}forClass> <{Vocabulary.Ampack}Catalog> ; "
            + $"<{Vocabulary.Solid}instanceContainer> <{TurtleText.Iri(container)}> .\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The pack containers a type index registers for the catalog class (at most <see cref="MaximumContainers"/>).</summary>
    internal static IReadOnlyList<string> Containers(IReadOnlyList<RdfTriple> index)
    {
        var registrations = index.Where(t => t.Predicate == Vocabulary.Solid + "forClass" && t.Object.IsIri(Vocabulary.Ampack + "Catalog")).Select(t => t.Subject).ToHashSet();
        return [.. index.Where(t => registrations.Contains(t.Subject) && t.Predicate == Vocabulary.Solid + "instanceContainer" && t.Object.Kind == RdfTermKind.Iri)
            .Select(t => t.Object.Value).Distinct().Take(MaximumContainers)];
    }

    // The storage: named in the profile, or the nearest container above the profile document that says it is one.
    private static async Task<string> StorageAsync(SolidSession session, WebIdProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Storages.Count > 0) return profile.Storages[0].EndsWith('/') ? profile.Storages[0] : profile.Storages[0] + "/";
        var path = Document(profile.WebId);
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
    /// What <paramref name="publisherWebId"/> offers this session: from its profile's public type index to its pack containers,
    /// then this reader's own catalog and the public one. Only containers on the publisher's own origins (its WebID document's
    /// and its storages') are read; entries are kept only when they name the archive and receipt by the digest, inside the
    /// container, in canonical form.
    /// </summary>
    internal static async Task<IReadOnlyList<CatalogEntry>> DiscoverAsync(SolidSession reader, string publisherWebId, CancellationToken cancellationToken = default)
    {
        var profile = await reader.ProfileAsync(publisherWebId, cancellationToken).ConfigureAwait(false);
        var origins = PublisherOrigins(profile);
        if (profile.PublicTypeIndex is not { } index || !IsCanonical(index) || !origins.Contains(SolidSession.Origin(index))) return [];
        var registrations = await reader.TurtleAsync(index, anonymous: true, cancellationToken).ConfigureAwait(false) ?? [];
        var found = new List<CatalogEntry>();
        foreach (var container in Containers(registrations).Where(c => IsCanonical(c) && c.EndsWith('/') && origins.Contains(SolidSession.Origin(c))))
        {
            reader.CheckUrl(container);
            var catalogs = new List<(string Url, bool Anonymous)> { (container + "public.ttl", true) };
            if (reader.WebId is { } me) catalogs.Insert(0, (container + "for/" + ReaderKey(me) + ".ttl", false));
            foreach (var (url, anonymous) in catalogs)
                if (await reader.TurtleAsync(url, anonymous, cancellationToken).ConfigureAwait(false) is { } triples)
                    found.AddRange(Catalog.Read(triples).Where(e => IsCanonicalEntry(e, container)).Select(e => e with { Publisher = publisherWebId }));
        }
        return found.DistinctBy(e => e.Digest).ToList();
    }

    /// <summary>
    /// Fetches one catalog entry and verifies it as this host: the archive and receipt on the publisher's own origins only;
    /// the pack in full (a set with every member) under <paramref name="policy"/>, naming the publisher discovery started from;
    /// the receipt for this URL and these bytes; and, unless <paramref name="recheck"/> is off, the keys that signed the pack
    /// and the receipt still asserted in the publisher's profile (deny only). Nothing is installed here.
    /// </summary>
    internal static async Task<FetchedPack> FetchAsync(SolidSession reader, CatalogEntry entry, TrustPolicy policy, bool recheck = true,
        DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var publisher = entry.Publisher ?? throw new ArgumentException("An entry names the publisher it was discovered from.", nameof(entry));
        var profile = await reader.ProfileAsync(publisher, cancellationToken).ConfigureAwait(false);
        var origins = PublisherOrigins(profile);
        foreach (var url in (string[])[entry.Archive, entry.Receipt])
            if (!IsCanonical(url) || url.Contains('?', StringComparison.Ordinal) || !origins.Contains(SolidSession.Origin(url)))
                throw new InvalidDataException($"{url} is not a canonical URL on {publisher}'s own origins; refused before any request.");
        var archiveResponse = await reader.SendAsync(HttpMethod.Get, entry.Archive, limit: reader.Options.MaximumArchiveBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!archiveResponse.IsSuccess) throw new InvalidDataException($"{entry.Archive} gave {(int)archiveResponse.Status}.");
        var receiptResponse = await reader.SendAsync(HttpMethod.Get, entry.Receipt, limit: Dsse.MaximumEnvelopeBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!receiptResponse.IsSuccess) throw new InvalidDataException($"{entry.Receipt} gave {(int)receiptResponse.Status}: a pack from a Pod comes with its receipt.");
        var archive = archiveResponse.Body;
        var verified = PackVerifier.VerifyWhole(PackReader.ReadArchive(archive, entry.Archive), policy, at);
        if (!verified.IsVerified) throw new InvalidDataException("The pack does not verify: " + string.Join(" ", verified.Problems));
        var pack = verified.Pack!;
        if (pack.Manifest.Federation is not { } federation) throw new InvalidDataException("A pack fetched from a Pod must be federated.");
        if (federation.Publisher != publisher) throw new InvalidDataException($"The pack names publisher {federation.Publisher}, not {publisher}, whose catalog listed it.");
        var (receipt, signedBy, problems) = PublicationReceipt.Verify(receiptResponse.Body, pack, archive, archiveResponse.Url, policy, at);
        if (receipt is null) throw new InvalidDataException("The receipt does not hold: " + string.Join(" ", problems));
        if (recheck)
        {
            // The profile can only take trust away: a key its owner no longer asserts, for the pack or for the receipt, refuses.
            var keys = pack.Signatures.Where(s => s.Role == "publisher").Select(s => s.KeyId).Append(signedBy!).Distinct();
            var withdrawn = keys.Where(k => profile.Keys.All(asserted => asserted.KeyId != k)).Select(Dsse.Short).ToList();
            if (withdrawn.Count > 0) throw new InvalidDataException($"{publisher} no longer asserts key {string.Join(", ", withdrawn)} that signed the pack or its receipt; refused.");
        }
        return new FetchedPack(pack, archive, receipt, receiptResponse.Body, archiveResponse.Url, signedBy!);
    }

    /// <summary>The origins a reader's credentials may reach for a publisher: its WebID document's and its storages'.</summary>
    internal static IReadOnlySet<string> PublisherOrigins(WebIdProfile profile)
    {
        var origins = new HashSet<string>(StringComparer.Ordinal) { SolidSession.Origin(Document(profile.WebId)) };
        foreach (var storage in profile.Storages)
            if (Uri.TryCreate(storage, UriKind.Absolute, out _)) origins.Add(SolidSession.Origin(storage));
        return origins;
    }

    /// <summary>
    /// A URL in the WebID grammar without a fragment, with no encoded or literal dot segments, that a URI library leaves exactly
    /// as it is: what a catalog may point at (FED-review H3: <c>%2e%2e</c> and the like resolve elsewhere than they read).
    /// </summary>
    internal static bool IsCanonical(string url) =>
        WebId.IsValid(url) && !url.Contains('#', StringComparison.Ordinal) && !DotSegment().IsMatch(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsoluteUri == url;

    /// <summary>An entry that names its archive and receipt exactly by its digest, inside the container.</summary>
    internal static bool IsCanonicalEntry(CatalogEntry e, string container) =>
        Digest().IsMatch(e.Digest) && PackManifest.IsValidId(e.Id) && SemVer.TryParse(e.Version, out _)
        && e.Archive == container + PublicationReceipt.ArchiveNameFor(e.Digest) && e.Receipt == container + PublicationReceipt.FileNameFor(e.Digest)
        && IsCanonical(e.Archive) && IsCanonical(e.Receipt);

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

    [GeneratedRegex("(?:%2[eE]){1,2}(?:/|$)|/\\.{1,2}(?:/|$)|\\\\", RegexOptions.CultureInvariant)]
    private static partial Regex DotSegment();

    [GeneratedRegex("\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();
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

    /// <summary>The entries of a catalog (at most <see cref="SolidPacks.MaximumEntries"/>), each subject's triples read once.</summary>
    internal static IReadOnlyList<CatalogEntry> Read(IReadOnlyList<RdfTriple> triples)
    {
        var bySubject = triples.ToLookup(t => t.Subject);
        string? Value(RdfTerm subject, string predicate, RdfTermKind kind = RdfTermKind.Literal) =>
            bySubject[subject].FirstOrDefault(t => t.Predicate == predicate && t.Object.Kind == kind)?.Object.Value;
        var entries = new List<CatalogEntry>();
        foreach (var pack in triples.Where(t => t.Predicate == Turtle.Rdf + "type" && t.Object.IsIri(Vocabulary.Ampack + "Pack") && t.Subject.Kind == RdfTermKind.Iri)
                     .Select(t => t.Subject).Distinct().Take(SolidPacks.MaximumEntries))
        {
            var id = Value(pack, Vocabulary.Dct + "identifier");
            var version = Value(pack, Vocabulary.Ampack + "version");
            var digest = Value(pack, Vocabulary.Ampack + "digest");
            var receipt = Value(pack, Vocabulary.Ampack + "receipt", RdfTermKind.Iri);
            if (id is null || version is null || digest is null || receipt is null) continue;
            entries.Add(new CatalogEntry(id, version, digest, pack.Value, receipt, Value(pack, Vocabulary.Dct + "title")));
        }
        return entries;
    }
}
