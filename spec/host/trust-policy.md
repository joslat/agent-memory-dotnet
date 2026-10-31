# Host document: the trust policy

**Version 1, draft.** Format strings: `agentmemory.pack.trust/1.1` (the trust policy), `agentmemory.pack.revocations/1.2`
(revocation documents). Requirement ids: `R-HOST-nnn`, numbered across the three host documents. Key words as in Core
§1.2.

A host's trust policy says which keys it trusts, for which pack ids and in which roles, what it has revoked, and which
roles a pack must be signed in. It implements the abstract trust model of Core §9. Host documents are versioned on their
own, apart from the pack format.

## 1. Reading the policy

**R-HOST-001.** A host MUST read its trust policy bounded to 4 MiB, as UTF-8; a policy that is not UTF-8 MUST be
refused.

**R-HOST-002.** A host MAY accept a policy that starts with one byte order mark or uses CRLF line ends: it removes
that one mark and reads each CRLF as LF, then reads the policy in the JSON profile (Core §6). A second byte order mark
MUST refuse the policy.

**R-HOST-003.** The policy MUST be closed at every level: a field this document does not define, at the top
level, in a key, in `revoked` or in a revoked key, MUST refuse the policy, naming the field.

**R-HOST-004.** A malformed policy MUST be refused before any pack is looked at.

## 2. Fields

| Field | Meaning |
|---|---|
| `format` | `agentmemory.pack.trust/1.1` |
| `keys` | The trusted keys (§3) |
| `revoked` | What the host revoked (§4) |
| `requireRoles` | Optional: the pack signing roles a pack must be signed in; the publisher alone when absent |
| `host` | Optional: `{webIds}`, who this host is (Federation profile) |

**R-HOST-005.** `format` MUST be `agentmemory.pack.trust/1.1`.

**R-HOST-006.** `requireRoles`, when present, MUST be a non-empty list of pack signing roles (Core §8.2).

**R-HOST-007.** `host`, when present, MUST be an object holding only `webIds`: 1 to 16 distinct WebIDs.

## 3. Keys

A key holds `keyId`, `publicKey`, `idPrefixes`, `roles`, and optionally `notBefore`, `notAfter` and `webId`.

**R-HOST-008.** `keyId` MUST be the SHA-256 of the key's DER SubjectPublicKeyInfo, as 64 lowercase hexadecimal
digits; a key MUST be listed once.

**R-HOST-009.** `publicKey` MUST be one PEM `PUBLIC KEY` block and nothing else: `-----BEGIN PUBLIC KEY-----`, a line
break, 1 to 16 lines of 1 to 76 base64 characters each ending in a line break (LF or CRLF; no blank line, no space),
`-----END PUBLIC KEY-----`, and at most one final line break. A private key or a second block MUST refuse the policy.

**R-HOST-010.** The block's content MUST be the one canonical P-256 key: the 91-byte DER SubjectPublicKeyInfo for
id-ecPublicKey on prime256v1 with an uncompressed point (`04`, then X and Y), in canonical base64 (no stray bits in the
last character), and the point MUST be on the curve. A compressed or hybrid point, another curve or another algorithm
MUST refuse the policy.

> Why: two implementations then always read the same key from the same text.

**R-HOST-011.** `idPrefixes` MUST be a non-empty list of id prefixes: lowercase segments as in an id, at most 64
characters, optionally ending with one `.`; a prefix covers ids by whole segments (Core R-CORE-058).

**R-HOST-012.** `roles` MUST be a non-empty list of key roles (Core §8.2); a `revoker` key and an `evaluator` key
MUST each hold that role alone.

**R-HOST-013.** `notBefore` and `notAfter`, when present, MUST be times (Core §6); `null` is not absent and MUST
refuse the policy.

**R-HOST-014.** `webId`, when present, MUST be a WebID (Federation profile), and a `revoker` key MUST NOT carry one.

## 4. Revoked

`revoked` holds `digests`, `keys` and `expires`.

**R-HOST-015.** `digests` MUST be a list of pack digests, each `sha256:` and 64 lowercase hexadecimal digits.

**R-HOST-016.** `keys` MUST be a list of objects holding `keyId` (64 lowercase hexadecimal digits) and optionally
`compromisedAt` (a time).

**R-HOST-017.** `expires`, when present, MUST be a time: the policy's own revocation freshness.

**R-HOST-018.** In this version no timestamp is judged, so a revoked key's signatures MUST NOT count, whatever
its `compromisedAt`; of two compromise times for one key, the earlier MUST hold.

## 5. Revocation documents

A revocation document distributes revocations to hosts, signed by a revoker.

**R-HOST-019.** A revocation document MUST be an envelope in the closed shape of Core §8.1 with the payload type
`application/vnd.agentmemory.revocations+json`, at most 4 MiB and 16 signature entries.

**R-HOST-020.** Its payload MUST be an object holding only `format` (`agentmemory.pack.revocations/1.2`),
`issuedAt` and `expires` (times, expiring after it was issued), `digests` (pack digests) and `keys` (revoked keys as in
§4).

**R-HOST-021.** A document MUST be refused unless a key the policy names as `revoker` signed it; a changed document
MUST NOT verify.

**R-HOST-022.** A document issued more than 5 minutes after now MUST be refused.

**R-HOST-023.** A revoker is *live* when it is valid now and revoked by nobody (the policy, or another counted
list). A list from a revoker that is not live (expired, revoked) MUST deny the packs and non-revoker keys it names for the
run only.

**R-HOST-024.** A list with a live signer MUST also revoke revokers, set freshness, and be kept by the ledger; it MUST
revoke a key against that key's will only when a live signer other than that key signed it.

**R-HOST-025.** One run MUST take at most 64 lists; one list carried in two envelopes MUST be counted once (by
what it signs, the SHA-256 of its pre-authentication encoding), with its signers together.

**R-HOST-026.** Freshness MUST be the newest live list's expiry (of two issued in the same second, the earlier
expiry), capped by the policy's own; a list MUST NOT extend the policy's own expiry.

**R-HOST-027.** A rotated revoker's successor carries its revocations forward: a list without a live signer that
revokes a revoker that has not expired, and that no counted list revokes, MUST refuse the run until a live list does. A
revoker MUST NOT escape this by revoking itself or by co-signing, and a successor not yet valid does not escape it.

**R-HOST-028.** Lists that revoke each other's signers in a circle MUST refuse the run; the same list given twice,
or reissued, is no circle.

**R-HOST-029.** Revocations MUST be added, never removed; of two expiries, the earlier holds; a list from the same
second, or an expired one, removes nothing.

**R-HOST-030.** The outcome of a run MUST NOT depend on the order in which its lists are given.
