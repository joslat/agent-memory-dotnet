# One core, every language

**The problem.** Memory semantics — supersession rules, temporal clocks, isolation boundaries,
ranking, rendering — are the part of an agent stack that must not drift. Every independent
reimplementation of them is a slow fork. Today those semantics live in four codebases, and no two of
them agree.

**The proposal.** Implement them **once**, in one compiled core (.NET, NativeAOT-ready). Every
language consumes that core through a thin native SDK — server mode today, embedded library mode
next — with **one conformance kit** refereeing that .NET, Python and TypeScript answers are
*byte-comparably the same*.

Fast where it matters (throughput, footprint, cold start). Equal where it doesn't — per-request
latency is I/O-dominated for everyone. Unique where it counts: **one semantics, provably shared.**

```
                 ONE authoritative engine (C#/.NET)
        semantics: supersession · two clocks · isolation ·
        ranking · projection · certificates · extensions
                │                          │
        Server backend (today)      Embedded NativeAOT (next)
        one binary/container         C ABI, 1–2 calls per op
                │                          │
     ┌──────────┼──────────┬───────────────┤
     ▼          ▼          ▼               ▼
   .NET      Python SDK   TS SDK        Go SDK (on trigger)
  (direct)   (pure, thin) (pure, thin)  + framework adapters
                    │
        ONE conformance kit (TCK) byte-comparing every path
```

The SDKs are thin **by design** — days of code, not engines. There is never "the Python version
doesn't do X yet", because there is no Python version of X. There is X, and there is a client.

---

## Feature parity, honestly — neither side has everything

The one-core pitch fails if it pretends the .NET engine is a superset today. It isn't, and the gaps
run **both ways**. Every cell is evidence-grounded, as of 2026-08-15.

| Capability | .NET engine (ours) | Python 0.5.0 (upstream) |
|---|:-:|:-:|
| Core memory ops, upstream schema | ✅ TCK 178/178 — base **and** all four extensions, same build, last run 2026-08-16 | ✅ |
| Reasoning traces | ✅ + measured procedural promotion | ✅ traces; no promotion tier |
| Bitemporal / point-in-time recall | ✅ two clocks, all kinds | ◐ preferences only; general case is open RFC #177 |
| Owner isolation on **reads** | ✅ central enforced policy | ✖ write-side identifier only (#137/#155 open) |
| Non-destructive supersession everywhere | ✅ | ◐ preferences only |
| Decay | ✅ shipped as re-ranking | ✖ open #42 |
| Trust levels + recalled-content admission | ✅ framework-stamped | ✖ proposed, unimplemented |
| Read-audit trail | ✅ `:MemoryReadAudit` | ✖ |
| Projection layer (scores, chains, quotes, dates) | ✅ | ◐ basic rendering; TS has three-tier injection |
| Schema extension system | ✅ | ✖ |
| Published accuracy benchmarks | ✅ bands, per-type ablations | ✖ |
| **Ontology tooling** (import/diff/migrate, templates) | **✖** | ✅ |
| **GDS algorithms** in adapters | **✖** | ✅ |
| **Python framework adapters** | **✖ — none** | ✅ 9 in-repo |
| **TS SDK + TS framework adapters** | **✖** | ✅ 4 adapters, real cadence |
| .NET framework adapters (MAF, Semantic Kernel, MEAI) | ✅ | ✖ |
| MCP server | ✅ | ✅ 16 tools |
| **Hosted service + console** | **✖ by design** | ✅ NAMS (Labs) |
| Extraction introspection | ◐ ingestion outcomes/stages | ✅ `get_extraction_status()` |

**Read honestly, this table is the argument _for_ one core, not against it.** Today capabilities are
scattered across four codebases and *nobody* has the full set. Ontology tooling exists only in Python.
Isolation and bitemporality only in .NET. Three-tier injection only in TypeScript.

Under one core each capability is built **once and appears everywhere** — the ontology tooling we'd
adopt, the isolation and temporal machinery you'd inherit, through thin SDKs and one conformance kit.
The alternative is this table growing more lopsided in both directions, forever.

---

## What you saw in the demo

| | Status |
|---|---|
| Bitemporal recall — same query, two instants, two answers | **shipped** |
| Non-destructive supersession — the replaced fact is still there and still linked | **shipped** |
| Owner isolation enforced on reads, verifiable from the response | **shipped** |
| Working-memory block + delta recall ("what changed since last session") | **shipped, off by default — effect on answers UNMEASURED** |
| Read-audit trail, unmoved by historical reads | **shipped** |
| LangGraph `BaseStore` adapter with an `as_of` filter | **prototype** — over a prototype host, draft wire |
| Python/TS SDKs, embedded NativeAOT, cross-language conformance arms | **design** |

The line between rows three and six is the one we care about keeping visible. What runs, runs. What
doesn't, is a drawing. And "shipped" is not one word: the two Wave-C rows are built, wired, and
tested — but they are off by default and their effect on answer quality has **not** been measured
yet, so we mark them differently from the rows a benchmark or a conformance kit already stands
behind. Our own memory map says the same thing in the same words.

---

> **Prototype notice.** The LangGraph adapter and the host behind this demo are throwaway
> prototypes built to answer one question cheaply. They are not published, not packaged, and not a
> preview of an API. The productized SDK follows the published designs.
