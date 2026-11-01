# Ampack Core

**Version 1, draft.** Format strings: `agentmemory.pack/1.2` (`pack.json`), `agentmemory.pack.files/1.1`
(`META/files.json`), `agentmemory.pack.card/1` (`card.json`). The public names of these strings are decided later.
Conformance classes: **Core Reader**, **Core Writer** (§2). Requirement ids: `R-CORE-nnn`.

## 1. Introduction

### 1.1 Scope

A *pack* is a set of files that carries data and declarations for an agent's memory: records, the schema that types them,
and declarations that optional profiles give meaning to. This document, Ampack Core, defines what every reader does to
establish that a pack holds exactly the bytes its publisher signed, and what those bytes contain at the level every
implementation shares: the container, the JSON profile, the signed list of files, the signatures, the trust model, the
versioning rules, how a pack's profiles are derived, the identity fields of `pack.json`, the kinds `knowledge` and `set`,
the record line format, the pack card and the attestation slot.

Profiles (the walk, the trial, composition, certification, federation, protection, code, permissions) are defined in
their own documents under `profiles/`. A host's own documents (trust policy, ledger, decision record) are defined under
`host/`.

A pack is data and declarations. Nothing in a pack runs when it is installed; code can join only through a profile a host
switches on.

### 1.2 Conventions

The key words "MUST", "MUST NOT", "REQUIRED", "SHALL", "SHALL NOT", "SHOULD", "SHOULD NOT", "RECOMMENDED", "NOT
RECOMMENDED", "MAY", and "OPTIONAL" in this document are to be interpreted as described in BCP 14 (RFC 2119, RFC 8174)
when, and only when, they appear in all capitals, as shown here. The profile and host documents use the key words in the
same sense.

Each normative statement is a numbered requirement, written with its id in bold at the start of its paragraph or list
item. Text in a "Why" note is informative. Tables generated from `registry.json` sit between `registry` markers and are
normative as the registry is.

### 1.3 Terms

- **Reader:** an implementation that reads packs; a *verifier* is a reader that decides whether a pack verifies.
- **Writer:** an implementation that builds and signs packs.
- **Host:** an application that loads verified packs into its memory, under its own trust policy.
- **Listed file:** a file named in `META/files.json`.
- **Pack digest:** `sha256:` followed by the lowercase hexadecimal SHA-256 of the exact bytes of `META/files.json` (§7).
- **Refuse:** to reject a pack (or a document) as a whole, with a reason, before anything of it is used.
- **Envelope:** a DSSE envelope (§8).
- **Character:** a Unicode code point. A text length is counted in characters wherever this specification bounds text, and
  text is ordered by code point (which is UTF-8 byte order); the one exception is JCS (RFC 8785), which orders object keys
  by UTF-16 code unit.
- **Id:** a pack, set or composite id in the grammar of §11.3.

## 2. Conformance

### 2.1 Core Reader

**R-CORE-001.** A Core Reader MUST apply every requirement of this document addressed to a reader or verifier,
in the order of §10, and MUST refuse a pack that breaks any of them.

**R-CORE-002.** Two Core Readers given the same pack, the same trust policy and the same time MUST reach the same
verdict, verified or refused.

**R-CORE-003.** A Core Reader that does not implement a profile a pack uses (§13) MAY still verify and
inspect the pack under this document, and MUST then say which profile it does not implement.

### 2.2 Core Writer

**R-CORE-004.** A Core Writer MUST write packs that satisfy every requirement of this document, and MUST NOT
write, sign or pass as lint a pack that a reader would refuse by a rule that needs no key (§10).

**R-CORE-005.** A Core Writer SHOULD produce the same `META/files.json` and the same archive bytes when
it builds the same content twice.

### 2.3 Limit classes

**R-CORE-006.** A reader in the **standard** limit class MUST accept every pack within the limits of §18 that
satisfies the other requirements, and MUST refuse a pack beyond them.

**R-CORE-007.** A reader in the **constrained** limit class (for browsers and small devices) MAY decline a
pack beyond limits it states, each no higher than the standard limit; it MUST then report the pack as beyond its limits,
not as refused, and MUST NOT verify a pack that a standard reader refuses.

> Why: a constrained reader cannot hold what a host can, but its verdict on what it does read must be the same. This
> draft defines no constrained numbers; implementations state their own.

## 3. Forms and identity

**R-CORE-008.** A pack MUST be readable in each of its forms: a folder, an `.ampack` archive (§4) and a member of a pack
set (§14.2). A pack's identity is its pack digest, and it MUST be the same in every form.

**R-CORE-009.** A reader MUST read a pack in folder form file by file under the same rules as an archive: every
file read with a bound, never through a link, never a folder where a file is named, and checked on what was opened rather
than only on the path before it was opened.

**R-CORE-010.** A reader MUST ignore, at the root of a folder, the version-control files `.git/`,
`.gitattributes` and `.gitignore`, and no other file.

**R-CORE-011.** When a listed file's hash fails in folder form, a reader SHOULD say when a line-ending
conversion to CRLF or a large-file pointer is the likely cause.

**R-CORE-012.** A writer that creates a pack folder SHOULD write a `.gitattributes` of
`* -text -filter -diff`.

> Why: a version-control checkout that converts line ends or replaces files with pointers changes the bytes and so the
> digest; naming the cause saves an operator from suspecting tampering.

## 4. The archive profile

An `.ampack` file is a ZIP archive in a strict profile.

**R-CORE-013.** The entries of an archive MUST be contiguous from offset 0, with no prefix, suffix, gap or
overlap; the archive MUST end with the end-of-central-directory record, with no comment and no trailing bytes.

**R-CORE-014.** Each local header MUST equal its central-directory entry (name, method, sizes, CRC, flags).

**R-CORE-015.** An entry MUST use method 0 (stored) or 8 (deflate). An entry MUST NOT use data descriptors,
encryption, extra fields or a comment; the archive MUST NOT hold directory entries, and MUST NOT use ZIP64.

**R-CORE-016.** Entries MUST be sorted by name, each name at most once; every entry MUST carry the fixed time
1980-01-01 00:00 and external attributes 0.

**R-CORE-017.** Every entry name MUST be a path in the grammar of §5.

**R-CORE-018.** A deflate stream MUST be valid, MUST end with its final block, and MUST end exactly where its
entry's bytes end.

**R-CORE-019.** A reader MUST count inflation against the entry's declared size and refuse an entry that inflates
beyond it, MUST refuse an entry whose declared compression ratio exceeds 250:1 before inflating it (a deflated entry of 0
bytes that declares content included), and MUST check every entry's CRC.

**R-CORE-020.** An archive MUST hold at most 4,096 files, each at most 64 MiB, at most 256 MiB in total, and MUST
be at most 512 MiB.

**R-CORE-021.** A reader MUST verify and import from the same bytes it read, and MUST NOT extract an archive
before it is verified.

> Why: a signed list of files is only as good as the agreement on which bytes are the files. Archives that two readers
> parse differently ("my ZIP is not your ZIP") are refused, so every reader sees one set of files.

## 5. Paths

**R-CORE-022.** A path MUST be 1 to 255 characters of at most 8 segments separated by `/`; each segment MUST
match `[A-Za-z0-9][A-Za-z0-9._-]{0,99}` (so at most 100 characters) and MUST NOT end with a dot.

**R-CORE-023.** A segment whose part before its first dot is, in upper case, one of the device names below MUST
be refused.

<!-- registry:paths.deviceNames -->
| Device name |
|---|
| `CON` |
| `PRN` |
| `AUX` |
| `NUL` |
| `CONIN$` |
| `CONOUT$` |
| `COM1` |
| `COM2` |
| `COM3` |
| `COM4` |
| `COM5` |
| `COM6` |
| `COM7` |
| `COM8` |
| `COM9` |
| `LPT1` |
| `LPT2` |
| `LPT3` |
| `LPT4` |
| `LPT5` |
| `LPT6` |
| `LPT7` |
| `LPT8` |
| `LPT9` |
<!-- /registry -->

**R-CORE-024.** Two files, two folders, or a file and a folder whose paths differ only by case MUST be refused, and
so MUST a file named like a folder.

> Why: a pack must unpack to the same tree on every file system, including ones that ignore case or alias device names.

## 6. The JSON profile

Every listed file whose name ends in `.json`, every line of a listed `.jsonl` file, `META/files.json`, every envelope and
every document this specification defines is read in the JSON profile.

**R-CORE-025.** A JSON text MUST be UTF-8 without a byte order mark and MUST NOT contain a carriage return.

**R-CORE-026.** An object MUST NOT repeat a key, at any depth.

**R-CORE-027.** Every string and every object key MUST be in Unicode Normalization Form C, decided by the Unicode
15.0 tables whatever the platform's own normalizer or globalization mode, and MUST NOT hold a lone surrogate.

**R-CORE-028.** A string or object key MUST NOT hold more than 30 combining marks in a row, counted as UAX #15
(Stream-Safe Text Format) counts them, on each character's NFKD decomposition.

**R-CORE-029.** Every number MUST be finite and representable as an IEEE 754 double, and every integer MUST be
within ±(2^53 − 1).

**R-CORE-030.** Arrays and objects MUST NOT nest deeper than 64.

**R-CORE-031.** A listed JSON file and a JSONL line MUST each be at most 1 MiB.

**R-CORE-032.** `META/files.json` and `META/lock.json` MUST be in RFC 8785 (JCS) canonical form and hold
integers only; a reader MUST refuse unless the bytes equal the canonical form of their parse.

**R-CORE-033.** A listed `.json` or `.jsonl` file under `assets/` MUST still be checked in the JSON profile, as
every listed JSON file is.

**R-CORE-034.** A date and time MUST be RFC 3339 written `YYYY-MM-DDTHH:MM:SS`, with an optional fraction of 1
to 7 digits, then `Z` or an offset `+HH:MM` or `-HH:MM` whose minutes are below 60 and whose size is at most 14 hours,
in ASCII digits; two times MUST be compared to 100 ns.

**R-CORE-035.** Base64 MUST be strict: the standard alphabet and padding only, no white space.

**R-CORE-036.** A version MUST be a SemVer 2.0 version in ASCII digits, at most 256 characters, each numeric
part fitting 64 bits, with no leading zero in a numeric prerelease identifier.

**R-CORE-119.** A unit-bearing field of a format document (the registry's `units.fields`) MUST be an integer written as one
(digits only: no fraction and no exponent, so `800000.0` and `8e5` are not), within ±(2^53 − 1). Its name ends in its unit: `Micro`, millionths of one; `Permille`, thousandths
from a closed set; `Ms`, whole milliseconds. It MUST be within the range, or among the values, the registry gives it.

**R-CORE-120.** Wherever this specification bounds text, the length MUST be counted in characters (code points); wherever
it orders text, the order MUST be by code point, except JCS's order of object keys (R-CORE-032).

> Why: a number two languages round differently, or a length one counts in UTF-16 units and another in code points, gives
> two verdicts on one pack. Integers in a named unit, and characters, read the same everywhere.

> Why: a field two readers read differently is an attack. One profile for every file, with no repeated keys, one
> normal form and bounded numbers and nesting, makes every reader in every language see the same values.

## 7. The files list and the digest

**R-CORE-037.** A pack MUST hold `META/files.json`, at most 2 MiB.

**R-CORE-038.** `META/files.json` MUST be an object holding exactly the fields below, with `format`
`agentmemory.pack.files/1.1`, an `id` in the grammar of §11.3, a `version` (§6), and `files`, an array of entries.

<!-- registry:documents.files.fields -->
| Field |
|---|
| `format` |
| `id` |
| `version` |
| `files` |
<!-- /registry -->

**R-CORE-039.** Each entry of `files` MUST hold exactly `path` (a path, §5), `length` (a non-negative integer)
and `sha256` (64 lowercase hexadecimal digits); entries MUST be sorted by path, each path at most once.

**R-CORE-040.** A pack MUST hold exactly the listed files, apart from `META/files.json` itself and the files under
`META/signatures/` and `META/attestations/`; a listed file missing, or an unlisted file present, MUST refuse the pack.

**R-CORE-041.** Every listed file MUST have exactly its listed length and SHA-256.

**R-CORE-042.** The pack digest MUST be computed over the exact bytes of `META/files.json`, never over a
re-serialization; signatures cover those exact bytes, and verifying MUST NOT re-canonicalize them.

**R-CORE-043.** The `id` and `version` of `META/files.json` MUST equal those of `pack.json`.

## 8. Signatures

### 8.1 Envelopes and statements

**R-CORE-044.** Every file under `META/signatures/` that is not a timestamp token (§8.4) MUST be an envelope; any
other file there MUST refuse the pack.

**R-CORE-045.** An envelope MUST be a DSSE envelope holding exactly `payloadType`, `payload` and `signatures`;
`payloadType` MUST be `application/vnd.in-toto+json`, `payload` strict base64, and `signatures` a non-empty array whose
entries each hold exactly `keyid` (64 lowercase hexadecimal digits) and `sig` (strict base64 decoding to 1 to 72 bytes).
This one closed shape holds for every envelope the format defines.

**R-CORE-046.** The payload MUST be an in-toto Statement v1 holding exactly `_type`, `subject`,
`predicateType` and `predicate`; `subject` MUST name exactly one subject, `META/files.json`, holding exactly `name` and
`digest` with exactly `sha256`, the SHA-256 of this pack's `META/files.json`.

**R-CORE-047.** A pack role's predicate MUST hold exactly `role`, `id`, `version` and `signedAt`; its
`predicateType` MUST be `urn:agentmemory:pack:<role>:v1` for a signing role of §8.2, `id` and `version` MUST be this
pack's, and `signedAt` MUST be a UTC time written `YYYY-MM-DDTHH:MM:SSZ`.

**R-CORE-048.** An envelope that does not sign this pack's list in a known role MUST refuse the pack.

**R-CORE-049.** A signature MUST be ECDSA P-256 with SHA-256 over the DSSE pre-authentication encoding of the
payload, encoded as strict DER; a signature that is not strict DER MUST NOT count.

**R-CORE-050.** A signature entry by a key the trust policy does not hold MUST be ignored; an entry by a
trusted key that does not verify, or does not count by §9.2, MUST be reported and MUST NOT count.

> Why: the role and the pack id are inside what is signed, not in a file name, so an envelope cannot be moved to vouch
> for another pack or another role. The signatures array itself is not signed: anyone may append an entry, and a
> malformed or untrusted one only fails to count.

### 8.2 Signing roles

Pack signing roles, and the roles a host's key may hold (the host roles are defined in `host/trust-policy.md`):

<!-- registry:roles.signing -->
| Signing role |
|---|
| `publisher` |
| `curator` |
| `assurer` |
<!-- /registry -->

<!-- registry:roles.keys -->
| Key role |
|---|
| `publisher` |
| `curator` |
| `assurer` |
| `revoker` |
| `evaluator` |
<!-- /registry -->

**R-CORE-051.** A pack MUST carry, for every role the trust policy requires (the publisher, unless the policy
names others), at least one signature that counts; otherwise the pack MUST be refused.

**R-CORE-052.** Curator and assurer envelopes MAY be carried; a reader MUST report them and they gate nothing
unless the trust policy requires their role.

### 8.3 Bounds

**R-CORE-053.** An envelope MUST be at most 64 KiB and hold at most 16 signature entries; a pack MUST carry at most
16 envelopes; a reader MUST hash each envelope's pre-authentication encoding once.

### 8.4 The timestamp slot

**R-CORE-054.** Beside an envelope `<envelope>`, a pack MAY carry at most one timestamp token
`<envelope>.tsr`, at most 64 KiB. A reader of this version MUST keep it and MUST NOT judge its content: it checks the
token's name, place and size only, and nothing in it is trusted or shown.

**R-CORE-055.** A token beside no envelope, or larger than 64 KiB, MUST refuse the pack.

**R-CORE-056.** A writer that writes a new envelope by the same key MUST drop the old token beside it.

> Why: the slot is reserved for trusted time (RFC 3161 tokens over an envelope's signature bytes) in a later minor, so
> a pack that carries tokens still opens in a reader of this one. Sixteen envelopes bound the slot to 1 MiB.

## 9. Trust

This section defines the trust model abstractly. The document a host keeps it in is `host/trust-policy.md`.

### 9.1 Keys

**R-CORE-057.** A key's id MUST be the SHA-256 of its DER SubjectPublicKeyInfo, as 64 lowercase hexadecimal digits.

**R-CORE-058.** A trusted key MUST be scoped by id prefixes and by roles: it MAY sign only for pack ids its prefixes
cover, by whole segments (a prefix covers the id equal to it and the ids below it at a `.`, never a lookalike), and only
in its roles.

**R-CORE-059.** A trusted key MAY carry a validity window (`notBefore`, `notAfter`).

**R-CORE-060.** A key in the revoker or evaluator role MUST hold that role alone.

### 9.2 Whose signature counts

**R-CORE-061.** A verified signature MUST count only when all of these hold: its key is in the role the statement
names; its prefixes cover the pack id; the signing time is not before `notBefore` and the time now is not before
`notBefore`; the signing time is not after `notAfter`; the time now is not after `notAfter`; the signing time is not more
than 5 minutes after now; and the key is not revoked.

**R-CORE-062.** The rule of R-CORE-061 MUST be the one rule for every signature the format defines:
pack envelopes, publication receipts and certificates alike (each with its own role).

> Why: a signing time is the signer's claim and can be back-dated, so a key's expiry is checked against now as well.
> Until a later minor judges timestamp tokens, an expired key's signatures no longer count.

### 9.3 Revocation

**R-CORE-063.** A host MUST be able to revoke pack digests and keys; a revoked digest MUST refuse the pack, and a
signature by a revoked key MUST NOT count.

**R-CORE-064.** Revocations distributed to hosts MUST be signed documents by a key in the revoker role, with an
issue time and an expiry; revocations MUST be added, never removed.

**R-CORE-065.** When the host's revocations have expired, a reader MUST refuse every pack until they are
refreshed.

**R-CORE-066.** A revoker's prefixes MUST NOT scope what it revokes: revokers are global.

### 9.4 Validity

**R-CORE-067.** A loaded pack MUST be valid until the first of: its own `validUntil`, its composite's and set's,
the revocation list's expiry and its signing keys' expiry. It MUST be valid through that instant, and off after it.

**R-CORE-068.** A reader MUST refuse a pack whose `validUntil` has passed.

### 9.5 The ledger

**R-CORE-069.** A host MUST keep a ledger that survives uninstall, recording for each id the highest version ever
installed and for each (id, version) its digest, and MUST refuse a downgrade, or a known version with another digest,
unless the operator says so. Its document is `host/ledger.md`.

> Why: without a ledger, an attacker who can make a host uninstall a pack can roll it back to an older, signed and
> vulnerable version.

## 10. Verification order

**R-CORE-070.** A reader MUST verify in this order, refusing at the first rule broken: (1) the archive profile and the
paths; (2) `META/files.json`, the listed files and their hashes, the signature folder and the envelopes; (3) the
signatures and trust; then (4) the JSON profile of every listed file, `pack.json` and its kind, the card, the records,
revocation and lifetime, and each implemented profile's rules (federation among them); and last (5) the ledger. Only then
MAY one record reach a store.

**R-CORE-071.** Before the signatures hold, a reader MUST NOT parse anything but `META/files.json` and the
envelopes. This holds for every reader and every load path, not only for a verifier: a host that must know a pack's kind
to choose how to load it MUST read the kind only once the signatures hold.

**R-CORE-072.** Every parser MUST refuse more than its bound, whoever calls it: the files list 2 MiB, an
envelope 64 KiB, `pack.json`, a card and a lock 1 MiB.

**R-CORE-073.** Every rule a reader applies before any trust MUST be applied as one check by every door that
reads a pack before it is trusted (lint, every writer's read-back, every command): the paths, the files list and the
content it lists, the signature folder, each envelope a DSSE envelope of a pack role's statement signing this list for
this pack (whether its keys count is judged once trust is applied), the JSON profile, `pack.json` and the kind; for a
set, the member rules of §14.2.

**R-CORE-074.** A door that acts on a pack under a host's policy (countersigning, locking, building a set, and
diffing or scanning given a policy) MUST verify the pack whole: a set with every member it carries, never on its outer
signature alone.

> Why: a reader that parses before it authenticates spends its effort, and exposes its parsers, on bytes nobody vouched
> for.

## 11. pack.json

### 11.1 Fields

**R-CORE-075.** `pack.json` MUST be an object whose fields are among those below (at the reader's own minor;
for a later minor, see §12).

<!-- registry:manifest.fields -->
| Field |
|---|
| `format` |
| `id` |
| `version` |
| `kind` |
| `sdk` |
| `requires` |
| `display` |
| `containableBy` |
| `contains` |
| `tier` |
| `licence` |
| `validUntil` |
| `protection` |
| `code` |
| `source` |
| `extensions` |
| `critical` |
<!-- /registry -->

| Field | Meaning |
|---|---|
| `format` | `agentmemory.pack/1.2`, or a later 1.x minor (§12) |
| `id`, `version` | A reverse-DNS id in the publisher's namespace (§11.3); a SemVer version |
| `kind` | The kind (§14) |
| `sdk` | The host SDK range the pack is written for |
| `requires` | `modules` (id → version range) and `host` features |
| `display` | `title`, `description`, `icon` (a path under `assets/`), `color` |
| `licence`, `tier` | An SPDX id or a URL; the tier |
| `contains`, `containableBy` | The hierarchy: what the pack contains; who may contain it and walk into it (Compose profile) |
| `source` | Where the content came from: `repository`, `commit`, `builtAt` |
| `validUntil` | After this instant a host switches the pack off |
| `protection` | The protection profile it declares (Protection profile) |
| `code` | The highest code tier it uses (Code profile) |
| `extensions` | Anything the format does not define (§11.5) |
| `critical` | Names of fields a reader must understand (§12) |

The closed values of the known fields:

<!-- registry:manifest.values -->
| Field | Values |
|---|---|
| `kind` | `knowledge`, `definition`, `snapshot`, `composite`, `set` |
| `tier` | `open`, `curated`, `premium`, `private`, `hosted` |
| `protection` | `open`, `audience`, `licensed`, `attested`, `hosted` |
| `code` | `E0`, `E1`, `E2` |
<!-- /registry -->

**R-CORE-076.** At the reader's own minor or an earlier one, a field of `pack.json` this document does not
define MUST refuse the pack, naming the field.

**R-CORE-077.** A value of `kind`, `tier`, `protection` or `code` outside the closed values above MUST refuse
the pack, at every minor.

### 11.2 Types

**R-CORE-078.** A field of `pack.json` of the wrong type MUST be refused, never read as absent. The fields
below MUST be strings; `requires` and `display` MUST be objects; `display.icon`, when present, MUST be a string;
`contains` and `containableBy` MUST be arrays of ids.

<!-- registry:manifest.stringFields -->
| String field |
|---|
| `format` |
| `id` |
| `version` |
| `kind` |
| `sdk` |
| `tier` |
| `licence` |
| `validUntil` |
| `protection` |
| `code` |
<!-- /registry -->

### 11.3 Identity fields

**R-CORE-079.** An id (of a pack, set or composite, and every entry of `containableBy`) MUST have at least three
lowercase segments separated by `.`, each starting with a letter and holding letters, digits and inner hyphens, and MUST
be at most 64 characters. Patterns that match it MUST anchor at the very end of the text, never before a final line
break.

**R-CORE-080.** A pack's id MUST be in its publisher's namespace, as the trust policy scopes the publisher's key
(R-CORE-058).

### 11.4 Display, source and validity

**R-CORE-081.** `display.title` MUST be at most 60 characters and `display.description` at most 300; `display.color`
MUST be `#RRGGBB`; an icon MUST be a PNG under `assets/`, 16 to 512 pixels a side and at most 64 KiB, and MAY appear only
on a knowledge pack.

**R-CORE-082.** `source` MUST be an object whose members are among `repository`, `commit` and `builtAt` (each optional),
each a string of 1 to 200 characters; `builtAt` MUST be a time (§6) and `commit` 7 to 64 lowercase hexadecimal digits.

**R-CORE-083.** `validUntil` MUST be a time (§6).

### 11.5 Extensions

**R-CORE-084.** A member of `extensions` MUST be ignored, unless `extensions.critical` names it.

**R-CORE-085.** `extensions.critical` MUST be a list of distinct names of members `extensions` holds (never
`critical` itself); a name the reader does not understand MUST refuse the pack.

The extensions this version understands:

<!-- registry:manifest.extensions -->
| Extension |
|---|
| `federation` |
<!-- /registry -->

## 12. Versioning

**R-CORE-086.** `format` MUST be `agentmemory.pack/1.` followed by a minor from 2 to 999 written without leading
zeros. A reader MUST state the minor it implements (this document: 2). Minors 0 and 1, another major, and any other
spelling (such as `1.02`, `1.2.0`, a trailing line break, or digits of another script) MUST NOT be read.

**R-CORE-087.** A pack of the reader's own minor or an earlier one MUST be read strictly: every field of
`pack.json` is one this minor defines, or the pack is refused, naming the field.

**R-CORE-088.** In a pack of a later minor, a top-level field of `pack.json` the reader does not know MUST be
ignored and the reader MUST say so, unless the pack lists it in `critical`, which MUST refuse the pack as needing a newer
reader.

**R-CORE-089.** `critical` MUST hold 1 to 64 distinct names of fields this `pack.json` holds; naming a field
it does not hold, or `critical` itself, MUST refuse the pack. A name the reader does not understand MUST refuse at any
minor; naming a field the reader knows is allowed and changes nothing.

**R-CORE-090.** The values of a known field MUST stay closed at every minor, and every document a reader
defines MUST keep its exact shape for all of 1.x. New meaning MUST come as a new `pack.json` field or a new file, never as
a new value or a new member of a known document; a changed document is a new file or a new major.

**R-CORE-091.** A listed file the reader does not define MUST be inert at every minor: no reader reads it as a
declaration. A later minor that gives a file meaning MUST declare it with a `pack.json` field, critical where an older
reader must not skip it.

**R-CORE-092.** The identity and trust layer MUST stay fixed for the whole major: the archive profile,
`META/files.json`, the envelopes and their statements, the signing roles and the attestation slot.

**R-CORE-093.** A field introduced by a later minor that bears on whether or how a pack is trusted, protected,
runs code, is federated, nests, expires, or is switched on by a trial MUST be listed in `critical` by its writer; a
reader of that minor MUST refuse a pack that uses such a field without listing it.

**R-CORE-094.** A writer MUST refuse to sign a `pack.json` of a later minor than it writes.

> Why: readers of one minor read a pack the same way, because they are strict where they claim to know it; readers of
> different minors differ only in fields the publisher declared optional, and every field that bears on trust is
> critical. Ignored fields are still signed, since `pack.json` is a listed file, so nothing unsigned rides in.

## 13. Profiles

A pack's profiles are derived from what it holds, by the registry's profile map: a pack uses a profile when it holds one
of the profile's files (a name ending in `/` covers the folder), one of its `pack.json` fields, one of its kinds, one of
its extensions, or one of its values of a field (a field that is absent takes its default: `protection` `open`, `code`
`E0`, `tier` `open`). There is no field in `pack.json` that lists profiles.

<!-- registry:profiles -->
| Profile | implemented | files | kinds | extensions | values | refusal |
|---|---|---|---|---|---|---|
| `walk` | yes | `traversal.json` |  |  |  |  |
| `trial` | yes | `evals/trial.json`, `evals/tests.json` |  |  |  |  |
| `compose` | yes | `members.json`, `META/lock.json` | `composite` |  |  |  |
| `federation` | yes |  |  | `federation` |  |  |
| `protection` | no |  |  |  | `protection`: `audience`, `licensed`, `attested`, `hosted` | {id} is protected as '{protection}'; this host opens 'open' packs only. |
| `code` | no | `queries.json`, `queries/`, `components.json`, `components/` |  |  | `code`: `E1`, `E2` | {id} carries code (tier {code}); this host runs declarative (E0) packs only. |
| `permissions` | yes | `policy.json` |  |  |  |  |
| `embeddings` | yes | `embeddings/` |  |  |  |  |
<!-- /registry -->

**R-CORE-095.** A reader MUST derive a pack's profiles from what it holds by the registry's profile map, in
the registry's order, and two readers MUST derive the same profiles for the same pack.

**R-CORE-096.** A host MUST refuse to load a pack that uses a profile the host does not implement, and a
profile the registry marks not implemented MUST be refused with the refusal words the registry gives it. A declared
restriction never loads as if it were absent.

**R-CORE-097.** A verifier SHOULD report the profiles it derived for a verified pack.

**R-CORE-098.** A profile introduced by a later minor MUST arrive as a critical `pack.json` field or a file a
`pack.json` field declares (§12).

> Why: a declared list of profiles beside the files and fields that already show them would be a second copy that a rule
> must keep equal to the first.

## 14. Kinds

The kinds are closed values (§11.1). This document defines `knowledge` and `set`; `definition` and `snapshot` are given
meaning by the Walk profile and `composite` by the Compose profile.

**R-CORE-099.** `pack.json` MUST name a kind.

**R-CORE-100.** A `knowledge` or `snapshot` pack MUST hold records under `records/` (files `records/*.jsonl`);
a `definition` pack MUST hold none.

**R-CORE-101.** A `composite` or `set` pack MUST NOT declare `sdk`, `requires` or `containableBy`: its
members carry their own.

### 14.1 knowledge

A knowledge pack holds declarations and its own records.

**R-CORE-102.** A host MUST import a knowledge pack's records only through its pack import, which holds at
most 5,000 records in at most 16 files, a line at most 64 KiB and nesting at most 8, shared types only, relations only
between two types of the same pack, and no `requires.modules`.

### 14.2 set

A pack set is one archive that carries complete packs side by side under `packs/<id>/`, each with its own
`META/files.json` and signatures, plus the set's own `pack.json` (kind `set`) and `META/` over everything.

**R-CORE-103.** A set MUST carry its members under `packs/`, every file under `packs/` MUST be inside a member
folder `packs/<id>/`, and the members MUST be exactly those the set's `contains` names.

**R-CORE-104.** Each member MUST verify on its own, by every requirement of this document; a member that
does not MUST refuse the set. The set's own signature binds the members together.

**R-CORE-105.** The pack in `packs/<id>/` MUST have the id `<id>`; a member MUST NOT have the set's own id; and a
member MUST NOT be a set.

**R-CORE-106.** A set MUST NOT hold an archive inside an archive.

## 15. Records

**R-CORE-107.** A record file MUST be JSONL: one JSON object per line, each line ending with a line feed
(including the last), no empty line, each line in the JSON profile.

**R-CORE-108.** A record MUST be an object of `type`, `key`, `properties` and `links`, as the record's module
declares them.

**R-CORE-109.** The member `labels` is reserved: a record that carries it MUST be refused until a profile
defines how a host enforces labels.

**R-CORE-110.** A host that writes records it read from elsewhere MUST put their text in NFC as it writes, and
MUST refuse text that still is not in NFC.

### 15.1 The record model

The model is neutral: typed records with properties and links, which a host binds to its own store (for AgentMemory,
`host/agentmemory-binding.md`). `ontology.json` declares types and relations; its other members are a host binding's.

**R-CORE-121.** A record's `type` MUST be a type the pack's `ontology.json` declares, or a built-in type; a link's
`relation` MUST be a relation declared from the record's type, or a built-in relation from a built-in type.

**R-CORE-122.** A property MUST be of one of these types:

<!-- registry:records.propertyTypes -->
| Property type |
|---|
| `string` |
| `int` |
| `float` |
| `bool` |
| `date` |
| `datetime` |
<!-- /registry -->

**R-CORE-123.** A pack MAY use these types and relations without declaring them; a record of a built-in type MUST hold
only its properties, each required one present:

<!-- registry:records.builtInTypes -->
| Built in type | text | properties |
|---|---|---|
| `note` | text | `text`: `type`: `string`; `required`: yes |
| `chunk` | text | `text`: `type`: `string`; `required`: yes; `index`: `type`: `int`; `required`: yes |
| `document` | title | `title`: `type`: `string`; `required`: yes; `uri`: `type`: `string` |
<!-- /registry -->

<!-- registry:records.builtInRelations -->
| Built in relation | from | to |
|---|---|---|
| `part_of` | chunk | document |
<!-- /registry -->

**R-CORE-124.** A type MAY name one of its string properties as its `text`, the record's main text (a built-in type names
its own, above); it MUST be in NFC (R-CORE-027), and its length is bounded, in characters, by the property's `maxLength`.

**R-CORE-125.** A type MAY declare a `memoryKind`, one of these, for mapping and display only: no rule of this
specification depends on it.

<!-- registry:records.memoryKinds -->
| Memory kind |
|---|
| `semantic` |
| `episodic` |
| `procedural` |
| `preference` |
| `identity` |
| `working` |
<!-- /registry -->

> Why: most memory products are text first, and a neutral model lets them map onto a pack without an ontology of their own.
> Kinds stay a mapping, never a taxonomy a rule depends on, because taxonomies age.

### 15.2 A record's life

**R-CORE-128.** A record MAY carry `validFrom` and `validTo` (when it is true in the world, `validTo` exclusive and after
`validFrom`), `expiresAt` (when a host stops using it, true or not) and `reviewBy` (when a person should check it), each an
instant in UTC to the second (`yyyy-MM-ddTHH:mm:ssZ`), and `supersedes`, 1 to 8 distinct keys of other records of its type in
the same pack. A record is **live at T** when its carrier is live (R-CORE-067) and `validFrom <= T < validTo` and
`T < expiresAt`, each bound absent meaning none.

**R-CORE-129.** A type MAY declare a `lifecycle`: `pinned`, whose records carry no `expiresAt`, or `refreshes`, with
`ttlDays` (1 to 36,500), whose records state `validFrom` and, without a `validTo`, end `ttlDays` after it.

**R-CORE-130.** A record superseded by another ends where the other begins: its `validTo` is the earlier of its own and the
other's `validFrom`, and when the other states none, it is never live. A record MUST be superseded at most once, the keys MUST
name records the pack holds, and supersession MUST NOT form a cycle. A reader resolves each record's effective life this way
before it stores the record, so a store keeps instants and no rule.

> Why: a fact is true for a while. A host that keeps a superseded price, or offers a decision after it was reversed, costs
> tokens and misleads; one that deletes it loses the history a two-clock store keeps.

## 16. The pack card

`card.json` is optional. It is a listed and signed file like any other, which a host's operator reads before installing.

**R-CORE-126.** Text a pack shows to a person (its card, `display.title`, which is one line, `display.description`, the
federation `purpose`, and every text a verifier checks as one) MUST be display text: display-safe, holding no code point in
these ranges, fixed at Unicode 15.0 whatever the platform's own tables; not blank (some character outside Unicode's
White_Space); and, when it is one line, holding no line feed, tab, line separator (U+2028) or paragraph separator (U+2029).
The ranges are the C0 and C1 controls but line feed and tab, every format character but ZWNJ and ZWJ (so every bidi control
and the zero-width space), private use and surrogates.

<!-- registry:text.displayRefused -->
| Display refused |
|---|
| `0000`, `0008` |
| `000B`, `001F` |
| `007F`, `009F` |
| `00AD`, `00AD` |
| `0600`, `0605` |
| `061C`, `061C` |
| `06DD`, `06DD` |
| `070F`, `070F` |
| `0890`, `0891` |
| `08E2`, `08E2` |
| `180E`, `180E` |
| `200B`, `200B` |
| `200E`, `200F` |
| `202A`, `202E` |
| `2060`, `2064` |
| `2066`, `206F` |
| `D800`, `F8FF` |
| `FEFF`, `FEFF` |
| `FFF9`, `FFFB` |
| `110BD`, `110BD` |
| `110CD`, `110CD` |
| `13430`, `1343F` |
| `1BCA0`, `1BCA3` |
| `1D173`, `1D17A` |
| `E0001`, `E0001` |
| `E0020`, `E007F` |
| `F0000`, `FFFFD` |
| `100000`, `10FFFD` |
<!-- /registry -->

> Why: what a person reads must be what a model reads. The format characters it refuses include the right-to-left and
> left-to-right marks, the soft hyphen and the tag characters, so some right-to-left and hyphenated text and the three
> subdivision flags (England, Scotland, Wales) are refused: tag characters are how text is smuggled past a reader. ZWNJ and
> ZWJ stay (Persian, Indic scripts, joined emoji), and so do variation selectors and keycaps, which are marks, not format
> characters.

> Why: signatures prove who wrote a text, not what it shows. A bidi override makes a title read otherwise than it is
> (Trojan Source), and a zero-width space hides a word; ZWNJ and ZWJ stay, because scripts and emoji need them.

| Field | Meaning |
|---|---|
| `format` | `agentmemory.pack.card/1` |
| `owner` | `{name, contact?}`: who answers for the pack (each 1 to 200) |
| `intendedUse` | What it is for (1 to 2,000) |
| `limitations` | What it is not for, and where it may be wrong (1 to 2,000) |
| `boundaries` | What a host must not do with it: at most 32 distinct texts of 1 to 500 |
| `risks` | The risks its publisher knows: at most 32 distinct texts of 1 to 500 |
| `provenance`, `audience`, `domain` | Optional: where its content comes from (2,000), who it is meant for (500), its domain (100) |
| `references` | Optional: at most 16 distinct texts of 1 to 500 |
| `whenToUse` | Optional, written for the model: what the pack knows and when to ask it (1 to 400; R-CORE-127) |

**R-CORE-111.** When present, `card.json` MUST be read strictly: `format` `agentmemory.pack.card/1`, the fields
above only (an unknown field MUST refuse the pack), `owner.name`, `intendedUse` and `limitations` required, each of its
type and within its length.

**R-CORE-112.** Card lengths MUST be counted in characters, and card text MUST be display-safe (R-CORE-126).

**R-CORE-127.** A card MAY hold `whenToUse`, written for the model: what the pack knows and when to ask it, display text of
at most 400 characters. Only a knowledge or definition pack's travels into the module the host composes, in its
declarations: a snapshot runs its definition's, and a composite or set composes its members'. A host MAY show it on every turn;
one that does MUST pass it through its admission of untrusted content first (R-HOST-056) and put it in a frame naming the
module, its version and the type `whenToUse` (R-HOST-053): it is the pack's own words, data, never an instruction. A trial
MUST count those framed bytes as the pack's fixed cost per turn (R-TRIAL-056), whether or not the host shows or admits it. A
listing that returns it as data (`pack_list`) MUST return the composed module's.

**R-CORE-113.** A writer SHOULD write a template card when it creates a pack, and SHOULD advise while a card is
still the template or when a knowledge or definition pack carries no card.

> Why: the card states; it is not checked against the content. Trials and certificates are what check content.

## 17. The attestation slot

`META/attestations/` lies outside the files list, so attestations can be added after release without changing the
digest. The Certify profile gives its content meaning.

**R-CORE-114.** `META/attestations/` MUST hold only `<stem>.certificate.dsse.json` and `<stem>.erasure.dsse.json` files and
`<stem>.aef/…` runs,
where a stem matches `[a-z0-9][a-z0-9-]{0,63}`; any other file there MUST refuse the pack.

**R-CORE-115.** The slot MUST hold at most 8 certificates, each at most 64 KiB; a run MUST travel beside its
certificate's stem, with at most 256 files and 8 MiB.

**R-CORE-116.** A Core Reader MUST check the slot's shape and bounds only, and MUST NOT trust anything in it: an
untrusted or malformed certificate MUST NOT refuse the pack.

**R-CORE-117.** A writer that rebuilds a pack with changed content MUST drop its attestations.

## 17a. Erasures

A publisher who shared a record can withdraw it from every host that imported it (feature 08).

**R-CORE-131.** An erasure MUST be a DSSE envelope of an in-toto statement of predicate type `urn:agentmemory:pack:erasure:v1`
whose predicate holds exactly `id` (the pack id, all its versions), `issuedAt` (an instant in UTC to the second, in ASCII digits), `reason`
(`withdrawn`, `incorrect`, `harmful` or `legal`), `salt` (32 lowercase hexadecimal characters) and `records`, 1 to 256 distinct
objects of `type` and exactly one of `key` (a string of 1 to 1,024 characters) or `keyHash` (64 lowercase hexadecimal characters, the
SHA-256 of the salt, a colon and the key in UTF-8), so a notice need not reveal what it erases. It travels in a pack's
attestations as `<stem>.erasure.dsse.json`, the stem its signer's key and issue time, or on its own.

**R-CORE-132.** A verifier MUST hold an erasure only when a signature verifies by a key the trust policy names as `publisher` or
`revoker` for the pack's id, valid at `issuedAt` and now, and the erasure names that pack. For a federated pack (§14) the key
MUST also be bound to the pack's publisher WebID, as its publisher signature is.

**R-CORE-133.** A host MUST apply every erasure it holds for a pack before it writes any of that pack's records, from any
version: each erased record's every version, and its content, MUST leave the store, and the host MUST keep a tombstone of the
pack, the type, the salt and the key's hash, never the key.

**R-CORE-134.** A host MUST NOT write, hold or offer a record a tombstone or an erasure it holds names, whichever version or copy
brings it; a link into an erased record goes with it, and the record that held it is still written. A host that holds an erasure
it has not yet applied to its store MUST still leave the records it names out of every read. An erasure for a pack names only
the records that pack carried (its own, or a snapshot's it carried), never another's of the same module, type and key.

**R-CORE-135.** On request, a host MAY sign a receipt that it applied an erasure it verifies: a DSSE envelope of an in-toto statement of
predicate type `urn:agentmemory:pack:erasure-receipt:v1` naming only the erasure statement's identity and when it was applied.

> Why: tombstones are known; a signed erasure bound to the pack's identity, held to the trust policy's roles, and read alike
> by both verifiers, is what a host can act on without trusting the channel it came through.

## 17b. Derivation

What a pack was made from, so a host can see where its records came from and trust them no further than that.

**R-CORE-136.** A pack MAY carry `META/derivation.json`, inside its signed content. When present, every verifier MUST read it
strictly and refuse the pack unless it is canonical (RFC 8785) and holds exactly `format` (`agentmemory.pack.derivation/1`),
`inputs` (1 to 256 objects of a `digest`, `sha256:` and exactly 64 lowercase hexadecimal characters and nothing after them,
a distinct `name` that is a display-safe line (§15) of 1 to 256 characters, and optionally a `licence`, a display-safe line of
1 to 128, and a `confidenceMicro`, an integer from 0 to 1,000,000), `tool` (a `name` and a `version`, each a display-safe
line of 1 to 128 characters), and `actions` (distinct, of `extract`, `consolidate`, `redact`, `translate` and `import`); and
optionally `model` (an `id`, a display-safe line of 1 to 256 characters, and optionally a `version` of 1 to 128), `promptDigest`
(a digest), `parameters` (at most 64 members, each a key of 1 to 128 characters and a value of 1 to 1,024, both display-safe
lines) and `parent` (a pack `id` and the `digest` of its file list). A parent is information, never trust: a derived pack is
admitted on its own signatures.

**R-CORE-137.** A knowledge pack's record line MAY carry `confidenceMicro` (an integer from 0 to 1,000,000) and `sourceKey`
(the `name` of an input of the pack's derivation; a key that names none refuses the pack). A snapshot's record lines and a
module bundle's carry neither, and a host MUST refuse one that does. A trust policy key that signs packs (as publisher, curator
or assurer) MAY carry `confidenceMicro`; every verifier MUST refuse a policy where another key does. When a host loads a pack it
MUST give each record the least of its own confidence, its source input's, and its signers': for each role the policy
requires, the cap of the most trusted key that signed the pack in that role, and of those the least; a part that is absent
caps nothing. It MUST NOT offer a record at a higher confidence. A host MAY set a floor: then no view and no walk offers a
record below it, a record without a confidence counting as 1,000,000, and a store MUST leave such records out before it
applies a limit, as it does a record not live. A framed record that has a confidence states it. The confidence is fixed when
the pack is loaded: a changed trust policy reaches records already imported when the pack is imported again.

> Why: inside the signed content, the derivation is part of the pack's identity and cannot be swapped; canonical, a number has
> one spelling, so both verifiers read the same confidence.

### 17c. Usage policy (Permissions profile)

**R-CORE-138.** A pack MAY carry `policy.json`, inside its signed content; a pack that does uses the Permissions profile. Every
verifier MUST read it strictly and refuse the pack unless it is canonical (RFC 8785) and holds exactly `format`
(`agentmemory.pack.policy/1`) and `rules`: 1 to 64 objects, each an `audience` of 1 to 16 distinct entries (`*`, everyone, or
a WebID), optionally `allow` and `deny` (distinct verbs of `read`, `derive`, `export`, `redact`, `rehydrate:local`,
`rehydrate:private`, `rehydrate:external` and `train`; at least one verb between them), and optionally `notAfter` (an instant
in UTC to the second). A verb is allowed for a host when a rule whose audience names everyone or one of the host's WebIDs
allows it and has not ended (now is at or before its `notAfter`), and no such rule denies it, ended or not: deny wins, and an
ended rule stops granting but never stops refusing. A verifier MUST refuse a pack its policy does not let the host read. A set
or a composite carries no policy of its own (its members carry theirs), and a verifier MUST refuse one that does.

**R-CORE-139.** A host MUST ask the policy at the door where each verb's action happens, at the time it acts, and MUST NOT act
when the verb is not allowed; `read` is a condition of every other verb. A host asks `export` where it serves a pack's records
to another program, and `rehydrate:<class>` where it sends a record's text to a model, by the class of the endpoint its
operator states (`local`, `private` or `external`; an endpoint of unstated class is `external`); a program it serves is a
model of the class its operator states for its clients. Every record a pack with a policy carries names that policy (the
`sha256:` digest of its `policy.json`) wherever it is stored, and a host MUST NOT offer a record whose policy it does not hold. `derive`, `redact` and `train` are declared; a host that derives from, redacts or trains on a pack asks them
too. Each verb maps to an ODRL action: `read` to `odrl:read`, `derive` to `odrl:derive`, `export` to `odrl:distribute`,
`redact` to `odrl:anonymize`, and `rehydrate:*` and `train` to `odrl:use` with the endpoint class or the training purpose as
its constraint.

> Why: a pack's owner says what a receiver may do, and a host enforces it where the action is, not by trusting the receiver's
> code to remember; inside the signed content, the policy cannot be swapped. A policy binds a host that honours this
> specification: it is evidence for audits and contracts, not a technical control over a host that ignores it.

## 18. Limits

The limits of the standard class. Text lengths are characters (code points).

| Where | Limit |
|---|---|
| Archive | 4,096 files; 64 MiB a file; 256 MiB in total; 512 MiB an archive; compression ratio at most 250:1 |
| Paths | 255 characters, 8 segments, 100 characters a segment |
| JSON | A listed JSON file or a JSONL line 1 MiB; `META/files.json` 2 MiB; nesting 64; 30 combining marks in a row; integers within ±(2^53 − 1) |
| Versions | 256 characters, each numeric part within 64 bits |
| Signatures | An envelope 64 KiB and 16 entries; 16 envelopes a pack; a timestamp token 64 KiB |
| Read documents | A trust policy 4 MiB; a key, a receipt or a certificate 64 KiB; an evidence file 8 MiB; the ledger 64 MiB; an activation record 1 MiB |
| Display | Title 60, description 300; icon PNG, 16 to 512 px a side, 64 KiB |
| Knowledge packs | 5,000 records in 16 files; a line 64 KiB; nesting 8 |
| Card | Texts 1 to 2,000 (owner 200, audience 500, domain 100); lists of at most 32 (references 16) distinct items of 1 to 500 |
| Attestations | 8 certificates; a run 256 files and 8 MiB |

**R-CORE-118.** Every file a reader or a host reads (an archive, a revocation list, a trust policy, a key, a
receipt, a certificate, an evidence file, the ledger, an activation record) MUST be read with its bound, never through a
link and never a folder where a file is named, checked on what was opened; a pipe or a device MUST be read with the same
bound, the limit checked before each block is kept; a file that cannot be read MUST be refused.

> Why: a bound checked on the path before opening can be beaten by swapping a link in between; a bound checked only on a
> declared size can be beaten by a stream that never ends.
