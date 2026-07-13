# Neo4j Memory Ecosystem — Compatibility & Quality

AgentMemory is an independent .NET implementation inspired by the Python
[`neo4j-labs/agent-memory`](https://github.com/neo4j-labs/agent-memory) reference project. Being a
separate implementation in a separate ecosystem only works if it stays honest about compatibility. This
page documents the two things that back that claim: how schema compatibility with the upstream project is
verified, and how the library itself is hardened before a release.

## Schema Compatibility — Schema-Parity and the TCK

Compatibility is checked on two independent axes: a **static structural check** against a frozen upstream
snapshot, and a **behavioral conformance suite** driven by the upstream project's own test kit.

### Static check — `agentmemory schema-parity`

```bash
agentmemory schema-parity [--upstream-version <v>]
```

Compares the .NET schema descriptor (`SchemaConstants`, `SchemaQueries`) against an embedded snapshot of
the upstream Python schema (default: the newest snapshot shipped with the CLI). No database connection is
needed — it's pure static analysis, safe to run in CI. It reports every divergence and classifies each one
(upstream-only label not implemented, .NET-only extension, .NET property superset, etc.) rather than just
pass/fail, so a divergence is a documented, intentional design choice, not a silent drift. Exit code is 0
when compatible, 1 on a real compatibility break.

### Runtime check — `agentmemory schema-check`

```bash
agentmemory schema-check
```

The runtime counterpart: verifies a **live** Neo4j database actually has every constraint and index the
schema bootstrapper is supposed to create. Exit 1 lists the missing objects. Where `schema-parity` asks
"does our schema *definition* still match upstream," `schema-check` asks "does *this* database match our
own definition" — the two catch different classes of drift.

See [`schema.md`](schema.md) for the full schema reference these commands verify against.

### Behavioral conformance — the TCK bridge

Structural parity doesn't prove behavior matches. For that, `tools/AgentMemory.TckBridge` is a thin HTTP
bridge implementing the upstream [`neo4j-labs/agent-memory-tck`](https://github.com/neo4j-labs/agent-memory-tck)
(Test Compatibility Kit) protocol, so the upstream Python conformance runner can drive this .NET
implementation out-of-process — one `POST` per adapter method, the same snake_case wire contract the TCK
expects from any implementation, .NET or otherwise.

| Tier | Scope | Result |
|---|---|---|
| **Bronze** | Schema and short-term memory | **93/93 passed** |
| **Silver** | Long-term search/lookup/relationships, reasoning memory | **67/67 passed** |
| **Gold** | Cross-memory integration (entity merge, similar-trace search) | **18/18 passed** |
| **Platinum** | Hosted-service operations | Out of scope (self-hosted library) |

**178/178** across Bronze, Silver, and Gold, verified against upstream `neo4j-labs/agent-memory-tck`
commit `4603b91f` driving the bridge over HTTP against a live Neo4j 5.26. See
[`tools/AgentMemory.TckBridge/README.md`](../tools/AgentMemory.TckBridge/README.md) for the endpoint
inventory and how to run the conformance suite yourself.

## Review Process — How the Library Is Hardened

AgentMemory went through multiple structured passes of adversarial review before each release milestone,
on top of normal PR review and CI:

- **Structured review cycles.** Six review cycles plus a capstone pass, each targeting a specific area
  (extraction/adapters durability and isolation, peripheral packages, GraphRAG/MCP correctness,
  Enrichment/samples) converged to zero outstanding findings.
- **Adversarial bug-hunting rounds.** Five further full-repo hunting rounds plus a dedicated convergence
  test — an explicit "is it actually perfect?" pass that re-audited the *consumers* of prior fixes rather
  than sampling new areas — together confirmed and fixed 80+ real defects (a mix of correctness,
  isolation/multi-tenancy, and boundary-guard issues), none left open.
- **API surface lockdown.** Ahead of the `1.0` cut, an eight-part review series audited the entire public
  API surface for implementation leakage, contract honesty, naming/enum consistency, and type safety, and
  locked the result under Semantic Versioning.
- **Release gates.** Every release ships from a warning-free Release build with the full test suite green:
  unit, Semantic Kernel, live-Neo4j integration, and performance smoke tests — currently **3000+ tests**
  in total — plus an end-to-end soak of the flagship sample against a live Neo4j instance.

The review process treats a passing happy-path test as insufficient evidence: fixes are verified by a
regression test that reproduces the original trigger (fails before the fix, passes after), and any
behavior-changing fix is followed by an audit of its own consumers, not just the code it touched — the
mechanism that let review converge to zero findings instead of resurfacing the same class of bug release
after release.
