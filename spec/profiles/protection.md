# Protection profile

**Version 1, draft.** Conformance class: **Protection Host**, one per protection level. Requirement ids: `R-PROT-nnn`.
Key words as in Core §1.2. Format strings: `agentmemory.pack.seal/1.2` (`META/seal.json`).

**This draft defines the declaration only.** A conforming host refuses every pack that uses this profile until a later
version of this document defines enforcement.

## 1. Introduction

### 1.1 Scope

Protection is a ladder, chosen per pack, honest about what each step does not stop. A pack declares its level in
`pack.json` `protection`. Level P0 (`open`) is Ampack Core itself; the other levels are declared here.

| Level | Pack carries | Host needs | Guarantees | Does not stop |
|---|---|---|---|---|
| P0 `open` | Signatures | A trust policy | Integrity, authorship, no rollback, proven use | Reading |
| P1 `audience` | `META/seal.json` and encrypted files | One recipient key (a person's, a host's, or an organisation's key in a KMS or HSM) | Only recipients read it; the publisher signature verifies without decrypting | A recipient copying |
| P2 `licensed` | P1, and a licence issued per licensee outside the pack | The licence and a renewed lease | Activation by proof of the host key; expiry; usage limits; offline licence files | A modified host ignoring limits |
| P3 `attested` | P1, with the recipient a key-release service | A trusted execution environment that proves itself by remote attestation | The key and content stay inside the enclave | Cost, cloud specifics |
| P4 `hosted` | Nothing ships | A connection | Nothing to copy | Latency |

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Protection profile when it holds `META/seal.json` or declares a
`protection` of `audience`, `licensed`, `attested` or `hosted`.

## 2. Requirements

**R-PROT-001.** A host that does not implement enforcement of a pack's protection level MUST refuse the pack, with the
refusal words the registry gives this profile; a declared protection MUST NOT load as if it were absent.

**R-PROT-002.** A pack that declares a protection other than `open` MUST carry `META/seal.json`.

## 3. The declaration of P1 (informative until enforced)

`META/seal.json`: `{"format": "agentmemory.pack.seal/1.2", "cipher": "AES-256-GCM", "recipients": [{"keyId", "hpke": "<RFC
9180 suite>", "enc", "wrappedKey"}], "sealed": ["records/...", ...], "contentDigest": "<sealed>"}`. In the design:

- each sealed file is replaced by its ciphertext (a random 96-bit nonce prefixed);
- `META/files.json` lists the ciphertexts, so the publisher signature verifies before decryption;
- the plaintext list's digest travels encrypted, so a recipient also verifies the plaintext;
- `pack.json`, `ontology.json` and `traversal.json` may stay clear, so a host can plan and show a pack before unlocking;
  a publisher may seal them too;
- a host unseals before the content checks (Core §10 step 4).

The licence of P2 is an envelope (`urn:agentmemory:pack:licence:v1`) by the publisher, carrying the pack id and a digest
range, the licensee key, features, `notBefore` and `notAfter`, a lease length, usage limits, and an activation endpoint
or none for offline licence files. Activation is a challenge the host signs with its key; revocation is not renewing the
lease.

Protections orthogonal to the levels, in the design: per-licensee watermark records (inert, never matching anchors,
committed by a salted Merkle root the publisher keeps); threshold unlock of the content key (M of N custodians, with an
audit record on break-glass); a time lock to a future drand round; opt-in usage receipts signed by the host (aggregate
counts per pack and period, no content, no personal data). Record labels and usage policies are the Permissions profile's;
the kill switch is Core revocation.
