# Memory pack format v1.1: conformance vectors

Two verifiers that share no code must reach the same verdict on every vector here:

- `ampack verify` (.NET, `tools/Ampack`, library `src/AgentMemory.Packs.Format`)
- `python/ampack_verify.py` (Python 3.10+ and `cryptography`)

`run_vectors.py` builds the vectors and runs both verifiers:

```
python conformance/pack-v1.1/run_vectors.py tools/Ampack/bin/Debug/net10.0/Ampack.exe
```

It builds a valid pack with `ampack`, builds another one entirely in Python (canonical files list, DSSE signature, strict
ZIP writer), and derives tampered and ambiguous variants:

| Kind | Vectors |
|---|---|
| Valid | a pack built by .NET; a pack built and signed by Python |
| Content | a changed record; an extra file; a missing file; a list rebuilt around changed content with the old signature |
| Identity | a list that is valid JSON but not RFC 8785 canonical; `pack.json` naming another pack than the list |
| Trust | a key the policy scopes to another namespace |
| JSON profile | CRLF in a record; a byte order mark; a repeated key |
| Paths | a Windows device name (`CON`) |
| ZIP profile | bytes before the first entry; bytes after the end record; an archive comment; unsorted entries; the data-descriptor flag; a local header whose name differs from the central directory |

`results.json` records each verdict and each verifier's reason. `trust.json` holds public keys only; the signing key
lives in a working folder that the run deletes.
