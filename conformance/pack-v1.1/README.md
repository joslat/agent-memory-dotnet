# Memory pack format v1.2: conformance vectors

Two verifiers that share no code must reach the same verdict on every vector here (255 today; exit code 1 is a refusal, any other failure is a defect and agrees with nothing):

- `ampack verify` (.NET, `tools/Ampack`, library `src/AgentMemory.Packs.Format`)
- `python/ampack_verify.py` (Python 3.10+ and `cryptography`); with `--receipt <file> --location <url>` it also verifies a publication receipt, and with `--certificate <META/attestations/… or file>` an evaluation certificate (`ampack certificate verify` on the .NET side)

`run_vectors.py` builds the vectors and runs both verifiers:

```
python conformance/pack-v1.1/run_vectors.py tools/Ampack/bin/Debug/net10.0/Ampack.exe
```

It builds a valid pack, a composite and a pack set with `ampack`, builds another pack entirely in Python (canonical files
list, DSSE signature, strict ZIP writers, stored and deflated), and derives tampered and ambiguous variants:

| Kind | Vectors |
|---|---|
| Valid | a pack built by .NET; a pack built and signed by Python; a deflated archive; a composite; a pack set; `validUntil` ahead; a full `source` |
| Content | a changed record; an extra file; a missing file; a list rebuilt around changed content with the old signature |
| Identity | a list that is valid JSON but not RFC 8785 canonical; `pack.json` naming another pack than the list |
| Strictness | an unknown field in `pack.json`; an extension that is ignored; a critical extension neither verifier understands; a critical list naming no extension |
| pack.json | an unknown kind, tier or code tier; `validUntil` passed, or not RFC 3339; a `source.commit` that is not hex; a `containableBy` that is not an id |
| Kinds | a knowledge pack without records; a definition pack with records; a protected pack without `META/seal.json` |
| Composites | a lock pinning another member; a lock that is not canonical; a lock with an extra field; `members.json` naming another member |
| Pack sets | a member changed under a re-signed set; a member in a folder named for another id; a file outside every member folder |
| Trust | a key scoped to another namespace; a key expired now (even though the signature claims a time in its window); a key not yet valid; an open window; a stale revocation list; a file under `META/signatures/` that is not an envelope |
| Revocations | signed documents applied by both verifiers: one that revokes nothing; one revoking the pack's digest; one revoking its key; one expired; one issued in the future; one signed by a publisher key, not a revoker; one changed after signing; one signed by the Python writer and applied by .NET |
| JSON profile | CRLF; a byte order mark; a repeated key; an integer beyond 2^53, and the smallest 64-bit integer; a number beyond a double; a string not in NFC; more than 30 combining marks in a row (and 30, accepted); nesting 65 deep (and 64, accepted); an empty line; no final line feed |
| Types and grammars (second red team) | a files-list entry with an extra field, or a length of `true`; `validUntil` as a number or beyond a 14-hour offset; `protection` null; `code` a list; `sdk` a number; `display` a string; a lock member id that is a number; a version with an Arabic-Indic digit; `source` of 100 and 101 astral characters (UTF-16 counted); base64 outside the alphabet or with a line break; a signing time without a leading zero or in lowercase; a second signature entry that is not base64 or has no `sig` (ignored); an extra envelope of wrong types (ignored); policies with no required roles, a key listed twice, a null prefix, a time that is not RFC 3339 |
| Paths | a Windows device name (`CON`); two entries that differ only by case |
| Bounds (fifth review) | a version of 256 characters (accepted) and 257; an envelope with 16 signature entries (accepted) and 17; a pack with 16 envelopes (accepted) and 17; an envelope over 64 KiB. A revocation document is at most 4 MiB, read with that bound |
| Signature encoding (sixth review) | an ECDSA INTEGER whose sign pad was dropped, on a pack and on a revocation list (the same list well padded is accepted): strict DER in both verifiers |
| Revokers that are not live (seventh review) | a list signed by a revoker the policy revokes: revoking nothing it verifies, revoking the pack it refuses (it still denies what it names, never decides about a revoker, brings no freshness) |
| Revokers that come back (eighth review) | a list without a live signer that revokes a revoker still live is refused (a live revoker carries it forward); a policy whose revoker key also signs packs is refused |
| One run, several lists (ninth review; Python's `apply_all` is a second implementation of the rule) | a rotated revoker's revocation of a revoker not carried forward (refused) and carried forward (verified); the named revoker revoking itself to dodge (refused); two revokers in a circle (refused); the same list twice (one list); a live list revoking the pack beside others (refused); a successor not yet valid named by a list without a live signer (refused) |
| Federated packs (federated packs §3–§4) | a pack for one buyer, verified by that buyer; refused by a host outside the audience and by a host naming no WebID of its own; a public pack on such a host (accepted); a key the host did not bind to the publisher WebID, or bound to another; the extension not marked critical; an audience empty, `"everyone"`, plain http, with a Turtle-breaking `>`, with two fragments, a WebID twice, 65 WebIDs; an unknown field; another format; no audience; a publisher that is not a WebID; a purpose of 300 (accepted) and 301 characters; policies with an empty or extended `host`, an http `webId`, a revoker bound to a WebID; a federated pack built by .NET |
| Publication receipts (federated packs §5; `ampack receipt verify` and `ampack_verify.py --receipt`) | a receipt signed by Python and one by `ampack receipt sign` (accepted); a placement narrower than the pack (accepted); another URL; other archive bytes; an audience wider than the pack's, or public for a named pack; a placement not for this host; a signer bound to another WebID, or not trusted; a payload edited after signing; an extra field; public access with named readers; an unknown access kind; a time in the future, or not in UTC; a location with a fragment; two subjects; `anonymous` among the allowed; a receipt for a pack that is not federated |
| Pack card (§15) | a full card (accepted) and none (accepted); an unknown field; no `intendedUse`; another format; a terminal escape in the owner's name; a C1 control; 1,000 astral characters (2,000 UTF-16 units, accepted) and one more; a boundary repeated; an empty owner name |
| Attestations area (§16) | a certificate whose content the pack check does not read (accepted: it is trusted only when a host verifies it); a stray file; an AEF run without its certificate; a certificate over 64 KiB; nine certificates; a stem in capitals |
| Evaluation certificates (§16; `ampack certificate verify` and `ampack_verify.py --certificate`) | signed by Python, by `ampack certify` from a host-signed trial decision, and as a separate file (accepted); an honest failure (accepted: the certificate says what happened); a host baseline with a confidence and an estimate; the AEF run beside it and matching, or travelling elsewhere (accepted); a tampered run; a run the certificate does not name; a run with another run id; a run.json outside the JSON profile (no id read, the run hash alone decides); a certified pack set verified whole (and refused when a member does not verify); a run file named as the certificate; a key not trusted, scoped elsewhere, expired, revoked, an assurer, or the publisher's; a policy whose evaluator key also signs packs (an evaluator key holds that role alone); a time in the future; another pack; another version; two subjects; edited after signing; passed with a missed check, or on nine tasks; arms of different sizes; a host baseline without its arm; more hits than tasks; a fraction where an integer goes; an extra field; an unknown target; an unknown confidence; an evaluator on two lines |
| ZIP profile | bytes before the first entry; bytes after the end record; an archive comment; unsorted entries; the data-descriptor flag; a local header whose name differs from the central directory; bytes after a deflate stream's end; a deflate stream without its final block |

`results.json` records each verdict and each verifier's reason. The `trust*.json` files hold public keys only; the signing
key lives in a working folder that the run deletes.

**Times** in a pack and in a policy are RFC 3339 date-times in both verifiers. **NFC** is decided the same way everywhere:
.NET under invariant globalization (as `ampack` runs) normalizes from tables generated from the Unicode data
(`eng/gen-nfc-tables.py`), checked against the platform on every code point.
