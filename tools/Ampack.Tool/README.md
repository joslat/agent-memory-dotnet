# Ampack.Tool: the `ampack` command

Verify, inspect and show **ampack** memory packs from the command line: knowledge, ontologies and minds for AI agents, in
one signed file that is checked where it lands.

This is a preview (0.1.0). It verifies and reads. It does not build, sign or certify a pack.

```
dotnet tool install --global Ampack.Tool
```

Needs .NET 8 or later.

```
ampack verify <pack.ampack> <trust.json> [<revocations.json>...]
ampack inspect <pack.ampack>
ampack show <pack.ampack> <trust.json> [<revocations.json>...] [--records]
```

| Command | What it does | Exit code |
|---|---|---|
| `verify` | Says whether **your** trust policy accepts the pack, and why not when it does not | 0 verified, 1 refused |
| `inspect` | Says what the file claims to be. No signature is judged: this is not a verdict | 0, or 1 when it is not a pack archive |
| `show` | Verifies, then shows the card, the types and the records. `--records` prints one JSON object a line | 0, or 1 refused |

Exit code 2 is a usage error or a defect, so a pipeline can tell it from a refusal.

A pack is accepted only when a key in your trust policy signed it as publisher for its id.

```
> ampack verify notes.ampack trust.json
verified io.example.notes 1.0.0 sha256:123c05b7b27f33b20a74b56cefaab49847d16d50b3c1b695af283479fa75643f
profiles core
```

The library behind it is the package `Ampack`. To use a pack inside a .NET program, take that one.

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
