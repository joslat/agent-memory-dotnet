# Memory pack format v1.2: conformance vectors

Two verifiers that share no code must reach the same verdict on every vector here (126 today; exit code 1 is a refusal, any other failure is a defect and agrees with nothing):

- `ampack verify` (.NET, `tools/Ampack`, library `src/AgentMemory.Packs.Format`)
- `python/ampack_verify.py` (Python 3.10+ and `cryptography`)

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
| ZIP profile | bytes before the first entry; bytes after the end record; an archive comment; unsorted entries; the data-descriptor flag; a local header whose name differs from the central directory; bytes after a deflate stream's end; a deflate stream without its final block |

`results.json` records each verdict and each verifier's reason. The `trust*.json` files hold public keys only; the signing
key lives in a working folder that the run deletes.

**Times** in a pack and in a policy are RFC 3339 date-times in both verifiers. **NFC** is decided the same way everywhere:
.NET under invariant globalization (as `ampack` runs) normalizes from tables generated from the Unicode data
(`eng/gen-nfc-tables.py`), checked against the platform on every code point.
