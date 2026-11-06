# Code profile

**Version 1, draft.** Conformance class: **Code Host**, one per code tier. Requirement ids: `R-CODE-nnn`. Key words as in
Core §1.2.

**This draft defines the declaration only.** A conforming host refuses every pack that uses this profile until a later
version of this document defines enforcement.

## 1. Introduction

### 1.1 Scope

A pack is data and declarations; nothing runs on install. Code can join only through the tiers below, each switched on
by the host. A pack declares the highest tier it uses in `pack.json` `code`.

| Tier | Files | Rules in the design |
|---|---|---|
| E0 declarative (the default) | Everything Core and the other profiles define | Nothing runs |
| E1 query templates | `queries.json` (name, purpose, parameters, language `gql` or `cypher`), `queries/*.gql` | Read-only clauses from an allow-list grammar; the pack's own labels only; a forced row limit; read-only transactions; the walk's read and time budgets |
| E2 WebAssembly components | `components.json` (interface: ranker, scorer, importer or storage policy; requested capabilities; memory and fuel limits), `components/*.wasm` | Listed and signed; capabilities granted one by one (no network or files by default); fuel and memory caps; plain WebAssembly modules with a small fixed interface where a runtime cannot host components yet |
| E3 native | Never inside a pack | A separately signed package referenced by coordinate and digest |

In the design, a host switches each tier on in its trust policy, and the Trial profile applies to every tier: code that
does not help stays off. The trust policy of this version defines no such switch.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Code profile when it holds `queries.json`, a file under
`queries/`, `components.json` or a file under `components/`, or declares a `code` of `E1` or `E2`.

## 2. Requirements

**R-CODE-001.** A host that does not implement a pack's code tier MUST refuse the pack, with the refusal words the
registry gives this profile; this holds for a declared tier above `E0` and for code files alike.

**R-CODE-002.** A pack MUST NOT carry native code; E3 is never inside a pack.
