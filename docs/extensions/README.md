# Schema extensions

A **schema extension** is a named, versioned, **additive-only** schema module — the unit AgentMemory
uses to add a capability's schema without touching the base schema every deployment shares.

Extensions are registered unconditionally and activated by id:

```csharp
services.AddNeo4jAgentMemory(neo4j =>
{
    neo4j.Uri = "bolt://localhost:7687";
    neo4j.Extensions.Add("procedural");
});
```

**Empty (the default) is the base schema, byte-identical.** An unknown id is rejected at startup,
listing the known ones — a deployment that asked for an extension and silently ran without it is the
failure this mechanism exists to make impossible.

## What an extension owns

| Piece | Where it lives |
|---|---|
| Declarations (properties per label, relationship types, labels) | The `ISchemaExtension` implementation |
| Migration scripts | `Schema/Migrations/ext/<id>/000N_name.cypher`, run after the whole base sequence |
| Migration bookkeeping | The existing `(:Migration)` node, version key `ext/<id>/000N_name`, plus `extension_id` |
| Parity divergence | The extension's `ParityDelta`, composed into the effective policy only while it is active |
| Ownership | `agentmemory schema-check`, which fails when a shape has no owner |

## The three rules

- **R1 — schema-additive-only.** An extension never renames, retypes, or repurposes a base shape.
  Enforced at build time over every registered extension, active or not, plus a lint over every
  `ext/` migration script (`CREATE … IF NOT EXISTS` or a `MATCH`-scoped backfill; no `DROP`, no
  `REMOVE`, no `DELETE`).
- **R2 — write-path isolation.** Extension data is written only through extension-specific APIs or
  flags. Upstream-parity surfaces never call them.
- **R3 — base-read neutrality.** Extension-written data is invisible or harmless on base read paths.

R2 and R3 are proven empirically per extension by the **Gold-under-extension gate**: the full
178-case TCK bridge run with the extension on, diffed against a same-build all-off control. Pass is
*identical results*; any difference is a violation and the extension does not ship. Run the treatment
arm with `--extensions <id,…>` on the bridge.

## Why migrations are namespaced

Two independently-written designs each named their migration `0012`, each correctly reasoning "next
free number after 0011". A database enabling one and then the other a month later would have had two
different scripts fighting over a single key in the unique-constrained `(:Migration {version})`
bookkeeping — one of them silently skipped as "already applied", leaving an index missing that nobody
could see was missing. Namespaced keys cannot collide with base names (a base name never contains
`/`), so the existing unique constraint keeps covering everything.

Base always runs first, and each namespace is internally linear. A database that enabled an extension
at base 0011 and later upgrades to a library shipping 0012 and 0013 replays those, then re-reaches the
extension's scripts and skips them through the ordinary applied-check.

## Shipped extensions

- [`procedural`](procedural.md) — the `trace_kind` promotion marker.
- [`working-memory`](working-memory.md) — the compiled per-owner profile block on upstream's `:User`.
- [`delta-recall`](delta-recall.md) — RANGE indexes over the clocks "what changed since I last looked?"
  seeks on. No labels, no properties, empty parity delta.
