# Federation profile

**Version 1, draft.** Conformance class: **Federation Host**. Requirement ids: `R-FED-nnn`. Key words as in Core §1.2.
Format strings: `agentmemory.pack.federation/1` (the extension); predicate type `urn:agentmemory:pack:publication:v1`
(publication receipts).

## 1. Introduction

### 1.1 Scope

A federated pack names its publisher by a WebID and the audience it is published for. A host takes it only when a key the
host bound to that WebID signed it as publisher and the host is in its audience. Placements of a pack on the Web are
vouched for by publication receipts. The first binding is to Solid (WebID profiles, Pods, WAC and ACP access control).
The profile can only take trust away: it adds conditions to Core, never removes one.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Federation profile when its `pack.json` holds the extension
`federation`.

### 1.3 Conformance

**R-FED-001.** A Federation Host MUST implement Ampack Core as a Core Reader, and every requirement of this document.

**R-FED-002.** A reader that does not implement this profile MUST refuse a federated pack rather than ignore
its audience.

## 2. The extension

The extension lives in `pack.json` as `extensions.federation`:

| Field | Meaning |
|---|---|
| `format` | `agentmemory.pack.federation/1` |
| `publisher` | The publisher's WebID |
| `audience` | `"public"`, or 1 to 64 WebIDs |
| `purpose` | Optional: why the pack is published |

**R-FED-003.** The extension MUST always be listed in `extensions.critical`; a pack that carries it without listing
it MUST be refused.

**R-FED-004.** The extension MUST be an object of the fields above only, with `format`
`agentmemory.pack.federation/1`, a `publisher` WebID and an `audience`; a field it does not define MUST refuse the pack.

**R-FED-005.** `audience` MUST be `"public"` or a list of 1 to 64 distinct WebIDs.

**R-FED-006.** `purpose`, when present, MUST be text of 1 to 300 UTF-16 code units.

**R-FED-007.** A WebID MUST be `https://` and a DNS host, or `http://` to loopback, then a path starting with `/` of
characters that need no escaping in Turtle, with at most one `#`, and at most 512 characters in all.

## 3. The host's trust policy

The trust policy fields are defined in `host/trust-policy.md`; this section gives their meaning here.

**R-FED-008.** A publishing key in the trust policy MAY carry the `webId` it is bound to; a revoker key MUST NOT
carry one.

**R-FED-009.** `host.webIds`, when present, MUST name 1 to 16 distinct WebIDs: who this host is.

## 4. Verifying a federated pack

**R-FED-010.** A federated pack MUST carry a counted publisher signature from a key the host bound to its
`publisher`; a trusted key bound to no WebID MUST NOT publish a federated pack.

**R-FED-011.** A counted publisher signature from a key bound to another WebID MUST refuse the pack, even
beside a signature from the right one.

**R-FED-012.** For an audience of named WebIDs, the host MUST be in the audience: a host whose `host.webIds`
names none of them, or that names no WebID of its own, MUST refuse the pack. A public pack needs no host WebID.

> Why: anyone holding a key this host trusts as publisher, bound to another WebID, can add an envelope and so veto a
> federated pack. Ignoring such signatures would let a pack count while a key the host bound elsewhere vouches for it
> under someone else's name. The host chose both keys, so a veto is the safer failure, and the remedy is the host's:
> narrow that key's prefixes.

## 5. Federated sets

**R-FED-013.** Every member of a federated set MUST be federated itself, by the same publisher, for an audience
within the set's.

**R-FED-014.** These member rules need no key, so a writer and lint MUST hold them too (Core §10).

**R-FED-015.** A federated set MUST be verified with every member it carries.

## 6. Publication receipts

A publication receipt vouches for one placement of a pack: where it was put, the exact archive bytes, and for whom.

**R-FED-016.** A receipt MUST be an envelope in the shape of Core §8.1 with 1 to 16 signatures, at most 64
KiB, whose statement has the predicate type `urn:agentmemory:pack:publication:v1` and exactly one subject:
`META/files.json` and its SHA-256.

**R-FED-017.** The predicate MUST hold no field other than `id`, `version`, `location`, `archiveSha256`,
`audience`, `access`, `checked` and `publishedAt`: the pack's id and version; the placement's URL; the archive's SHA-256
(64 lowercase hexadecimal digits); the placement's audience; its access kind; the probe's record; and the publishing time
in UTC written `YYYY-MM-DDTHH:MM:SSZ`.

**R-FED-018.** `location` MUST be a URL in the WebID grammar without a fragment.

**R-FED-019.** `access` MUST be one of the access kinds below, and MUST be `public` exactly when the audience
is public.

<!-- registry:federation.accessKinds -->
| Access kind |
|---|
| `wac` |
| `acp` |
| `public` |
<!-- /registry -->

**R-FED-020.** `checked` MUST be an object of `denied` and `allowed`, each a list of at most 64 distinct WebIDs
(`anonymous` may be denied), and MUST agree with the placement: a receipt for named readers records that `anonymous` was
refused, records no reader outside its audience as allowed and no reader in it as refused; a public receipt records nobody
as refused.

**R-FED-021.** A receipt MUST be for a federated pack only.

**R-FED-022.** A receipt MUST hold only for this pack (its digest, id and version), at the location the pack was
fetched from, for the exact archive bytes fetched, and for a host in the placement's audience.

**R-FED-023.** A placement MUST only narrow the pack's audience: a receipt for a wider audience than the pack's,
or a public placement of a pack for named WebIDs, MUST be refused. A public pack MAY be placed publicly or for named
readers.

**R-FED-024.** A receipt MUST be signed, with the publisher role under Core §9.2 and the publishing time as the
signing time, by a key bound to the pack's publisher WebID.

**R-FED-025.** A fetch from a Pod MUST require a receipt; a set's receipt MUST hold only when every member
verifies.

**R-FED-026.** A publisher's tool MUST NOT sign a receipt whose probe record disagrees with its placement.

## 7. On the Web: the Solid binding

**R-FED-027.** A publisher's key MUST be asserted in its WebID profile (`sec:assertionMethod`, as a P-256 JWK).

**R-FED-028.** Discovery SHOULD start from the publisher's public type index; catalogs are per reader, each naming
its publisher.

**R-FED-029.** A reader MUST present its credentials only in the publisher's own places: its WebID
document's origin, and storages elsewhere only when they name the WebID as owner; never an IP address.

**R-FED-030.** A publisher's tool MUST write access in the model the server uses, with denies in ACP so that
nothing inherited reaches the pack, and MUST probe it.

**R-FED-031.** On the Web, a reader MUST bound documents to 1 MiB, a type index to 8 containers, and a catalog to
1,000 entries.
