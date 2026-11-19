# Ampack for .NET

Verify and read **ampack** memory packs: knowledge, ontologies and minds for AI agents, in one signed file that is
checked where it lands.

This is a preview (0.1.0). It verifies, inspects and reads. It does not build, sign or certify a pack.

```
dotnet add package Ampack
```

It depends on .NET alone (8, 9 or 10): no AgentMemory, no database, no agent framework. Use it from any host.

## Verify a pack

A pack is accepted only when a key in **your** trust policy signed it as publisher for its id.

```csharp
using Ampack;

var verdict = Pack.Verify("notes.ampack", "trust.json");
Console.WriteLine(verdict.Verified
    ? $"{verdict.Id} {verdict.Version} {verdict.Digest}"
    : "refused: " + string.Join(" ", verdict.Reasons));
```

## Read a pack

```csharp
using Ampack;

try
{
    var pack = Pack.Load("notes.ampack", "trust.json");
    Console.WriteLine($"{pack.Id} {pack.Kind} {pack.Title}");
    foreach (var record in pack.Records())
        Console.WriteLine($"{record.Type} {record.Key} {record.Properties.Count} properties");
    foreach (var member in pack.Members)          // a pack set carries several packs
        Console.WriteLine($"{member.Id} {member.Kind} {member.Digest}");
}
catch (PackRefusedException refused)
{
    Console.WriteLine("not loaded: " + refused.Message);
}
```

Nothing is returned from a pack that did not verify. Every method also takes the bytes themselves, with revocation
documents and the moment to judge at in `PackOptions`. `Pack.Inspect` describes an archive without judging any signature:
never treat it as a verdict.

## What it checks, and what it does not

- **Checked, as the specification says:** the strict archive, every file's length and SHA-256 against the signed list,
  the signatures against the trust policy (key scopes, roles, validity windows, revocations), `pack.json`, the card,
  composites and their locks, pack sets and each member, embeddings, the usage policy's read door.
- **Checked on each record line:** its shape only. An object with a string type and key, properties as an object, links
  as `{"relation", "target"}` pairs, no field the format does not define, and a knowledge pack's limits (5,000 records,
  16 files, 64 KiB a line, nesting 8).
- **Not checked by this reader:** whether a record fits the pack's ontology (its type, required properties, relations).
  A host that imports records checks that.

This package is a reader-only build of the format's .NET verifier, the one the conformance vectors are run against. It
shares no code with the Python and Rust verifiers, and must reach the same verdict as they do on every vector.

## Links

- The format, the hub and certified packs: https://memcert.dev
- Specification and test vectors: https://github.com/joslat/agent-memory-dotnet/tree/main/spec
- Paper: https://doi.org/10.5281/zenodo.24012989

Apache-2.0. Patent pending (Swiss application CH001531/2026); see `PATENTS.md` in the repository for the proposed
pledge.
