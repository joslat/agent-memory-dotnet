# ampack (the `ampack-rs` command)

Verify and inspect **ampack** memory packs from the command line: knowledge, ontologies and minds for AI agents, in one
signed file that is checked where it lands.

```
cargo install ampack
```

The command is named `ampack-rs`, so it never hides another `ampack` command on the same machine.

```
ampack-rs verify <pack.ampack> <trust.json> [<revocations.json>...]
          [--receipt <file> --location <url>] [--certificate <path>] [--erasure <path>] [--aggregate <file>]
ampack-rs inspect <pack.ampack>
```

Exit code 0 means verified, 1 refused (with the reason on the line), 2 a usage error or a defect. A pack is accepted only
when a key in **your** trust policy signed it as publisher for its id. `inspect` judges no signature.

It verifies and inspects. It does not build, sign or certify a pack. The library behind it is the crate `ampack-core`.
This is a preview (0.1.0).

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
