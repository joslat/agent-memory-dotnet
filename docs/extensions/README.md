# Schema extensions

A **schema extension** is a named, versioned, **additive-only** schema module — the unit AgentMemory
uses to add a capability's schema without touching the base schema every deployment shares.

## Why this exists

**Adding a memory component updates the brain.** A new memory capability is rarely just code: it wants
a property, sometimes an index, occasionally a relationship type — and every one of those lands in a
graph that other deployments, and an upstream Python implementation, also have opinions about. Before
this system there was no unit for that, and the consequences were already concrete rather than
hypothetical:

- **Migration numbers collided.** Two independently-written designs each claimed `0012` as "next free
  after 0011", each correctly. A database enabling one and then the other a month later would have had
  two different scripts fighting over a single key in the unique-constrained `(:Migration {version})`
  bookkeeping — one silently skipped as "already applied", leaving an index missing that nobody could
  see was missing.
- **Divergence had no owner.** `trace_kind` shipped in migration `0011` with its entire rationale in a
  Cypher comment. The parity policy allowed it, and the parity policy, the CLI and the docs all knew
  nothing about *which feature* owned it. One feature made that survivable. Five would not.
- **Parity changes were prose.** "Remove `User` from `UpstreamOnlyLabels`, add three properties" was an
  instruction in a design document, with no machine link to the feature that justified it and no way to
  un-edit it if the feature was abandoned.

An extension is the answer to all three: a name that namespaces its migrations, an owner for every
shape it introduces, and a machine-checkable parity delta that applies **only while it is active**.

## Enabling one

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

Three behaviours worth knowing:

- **Registration is not activation.** Every shipped extension is registered in DI unconditionally,
  because gating the registration on a flag means a host that flips the flag later through `IOptions`
  reconfiguration still gets nothing — and the failure is silent. This is the same lesson the
  `IMemoryReranker` registrations learned.
- **Dependencies are activated implicitly.** Asking for an extension gets you what it needs to work.
  Refusing until every dependency is named explicitly would turn a solvable configuration into a
  startup failure for no safety gained, since the dependency is additive schema either way. (No shipped
  extension declares a dependency today.)
- **Order is deterministic, always.** Active extensions are returned topologically sorted with ties
  broken by ordinal id, because that order decides the sequence migration scripts run in — and a
  migration order that varied per process would be a schema that varied per process. A dependency cycle
  is rejected outright and nothing is applied.

## Who runs the DDL

**Registering an extension in code does not create its schema.** The two are deliberately separate:
application startup should not be allowed to run DDL against a shared database. So an extension's
`ext/<id>/000N` scripts are applied by whoever owns the deployment's schema, with the operational CLI:

```bash
# base only — what `migrate` has always done
agentmemory migrate --uri bolt://db:7687 --password s3cret

# base + the named extensions' ext/<id>/000N scripts
agentmemory migrate --uri bolt://db:7687 --password s3cret --extensions arithmetic,delta-recall

# who owns which shape on this database, and is anything missing?
agentmemory schema-check --extensions arithmetic,delta-recall
```

`--extensions` resolves through the same precedence as every other connection setting — CLI option >
`Neo4j:Extensions` > `NEO4J_EXTENSIONS` > empty — and applies to **every** database-backed command, so
`schema-check` reports on the same set `migrate` applied rather than a different one.

**Forgetting this step does not produce an error**, which is exactly why it has its own section. An
application that enables `arithmetic` against a database whose `ext/arithmetic/0001` was never applied
keeps working: the MERGE still converges on one node, the queries still return correct results, and the
only symptom is a full scan where an index seek belonged. Nothing fails, so nothing gets investigated.

The ordering rule follows from the [namespacing](#why-migrations-are-namespaced) below: base always runs
first, each `ext/<id>/` namespace is internally linear, and re-running is a no-op. Enabling an extension
later on a database that has already migrated simply applies that extension's scripts and leaves
everything else alone.

## What an extension owns

| Piece | Where it lives |
|---|---|
| Declarations (properties per label, relationship types, labels) | The `ISchemaExtension` implementation |
| Migration scripts | `Schema/Migrations/ext/<id>/000N_name.cypher`, run after the whole base sequence |
| Migration bookkeeping | The existing `(:Migration)` node, version key `ext/<id>/000N_name`, plus `extension_id` |
| Parity divergence | The extension's `ParityDelta`, composed into the effective policy only while it is active |
| Ownership | `agentmemory schema-check`, which fails when a shape has no owner |

### The parity delta

The base policy `SchemaParityPolicy.Upstream_0_5_0` is what keeps this port honest against upstream
`neo4j-agent-memory v0.5.0`: it names the labels only .NET has, the relationship types only .NET has,
the properties .NET adds on top of upstream's, and the labels only *upstream* has. A `SchemaParityDelta`
is the exact, machine-checkable change one extension makes to it, across five axes:

| Axis | Means |
|---|---|
| `AddNetOnlyLabels` | a label this extension introduces that upstream does not have |
| `AddNetOnlyRelationshipTypes` | likewise for relationship types |
| `AddNetSupersetProperties` | properties .NET has that upstream does not |
| `RemoveUpstreamOnlyLabels` | an upstream-only label this extension **adopts** — divergence *narrowing* |
| `ReserveUpstreamPropertyNames` | names we ask upstream not to take for something else |

`SchemaParityPolicy.WithExtensions(active)` composes the active deltas into an **effective** policy and
is a pure function — the shared static base policy is never mutated, and an empty active set returns it
unchanged. The verifier itself is untouched; it already took the policy as a parameter.

Composition can fail, and the failures are the point. Removing an upstream-only label that the base
policy does not list as upstream-only throws — the delta is stale, so either the label was already
adopted or it never existed upstream. Two extensions adding the same net-only label or relationship
type throws — a shape with two owners cannot be reported to one. Superset **properties** are the
deliberate exception: they are additive and may legitimately be declared by more than one extension.

One axis is documentary rather than enforced: `ReserveUpstreamPropertyNames` is a request to upstream,
not a check on us, and nothing in the composition consumes it. It is asserted only by the
documentation test, which requires it to be named on the extension's page.

## The three rules

- **R1 — schema-additive-only.** An extension never renames, retypes, or repurposes a base shape.
  This is the one rule with **code enforcement**, in three places: identity (id shape, version number,
  migration-script naming) is checked at **startup** by the registry; the full disjointness check —
  nothing an extension declares may already belong to base, and no two extensions may declare the same
  shape — runs in the **unit suite on every build**, over every *registered* extension, active or not,
  because an extension that collides with base is broken whether or not anyone has switched it on yet;
  and a lint over every `ext/` migration script requires each statement to be a
  `CREATE … IF NOT EXISTS` schema object or a `MATCH`-scoped backfill, refusing `DROP`, `REMOVE`,
  `DELETE` and `DETACH DELETE`.
- **R2 — write-path isolation.** Extension data is written only through extension-specific APIs or
  flags. Upstream-parity surfaces never call them.
- **R3 — base-read neutrality.** Extension-written data is invisible or harmless on base read paths.

R2 and R3 have **no code enforcement and are not claimed to**. They are proven empirically per
extension by the **Gold-under-extension gate**: the full 178-case TCK bridge run with the extension on,
diffed against a same-build all-off control. Pass is *identical results*; any difference is a violation
and the extension does not ship. Run the treatment arm with `--extensions <id,…>` on the bridge.

**Evidence to date: 178/178 on both arms** — the all-off control and the with-extensions treatment —
with no counter drift between them. That is the strongest statement this gate can make and it is worth
reading precisely: it says activating these extensions changes nothing a conformance run can observe.
It does not say the extensions were exercised by the run. Where an extension has a reason its own code
is unreachable from the bridge, its page says so ([`delta-recall`](delta-recall.md) and
[`arithmetic`](arithmetic.md) both do) — and where the gate *is* load-bearing rather than ceremonial,
its page says that too ([`working-memory`](working-memory.md)'s ownerless-write guard is the case: the
bridge writes are ownerless, so without the guard three cases turn into 500s).

No extension has declared a TCK case profile of its own yet. `TckProfileDescriptor.None` is a real,
validated state rather than a placeholder — declaring a case folder that does not exist would be
exactly the ship-but-unreachable defect this codebase keeps catching, and a declared profile with a
minimum case count of zero is rejected outright.

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
- [`arithmetic`](arithmetic.md) — the session accountant's materialised aggregates: `fact_kind='derived'`
  on `:Fact` plus one `DERIVED_FROM` edge, for answers that must be computed rather than found.

Four ids, frozen: `arithmetic`, `delta-recall`, `procedural`, `working-memory`. An id is load-bearing
inside `(:Migration).version` keys, so renaming one orphans applied migrations on every database that
has them — which is why a test pins the list.

## The owners report

`agentmemory schema-check --extensions <ids>` answers a question the parity verifier never could.
The verifier asks *"is this shape allowed?"*; the owners report asks **"whose shape is this?"** — and
fails when nothing can answer.

```
schema-check: policy base 0.5.0 + extensions: [arithmetic v1, procedural v1]
  property     Fact.fact_kind                           owner: arithmetic
  relationship DERIVED_FROM                             owner: arithmetic
  property     ReasoningTrace.trace_kind                owner: procedural
  property     User.working_memory                      owner: working-memory   (registered, not active)
  applied      ext/arithmetic/0001_derived_fact         owner: arithmetic  2026-08-16T…
schema-check: every non-base shape names an owner (N shape(s) attributed).
```

Every **registered** extension is described, not only the active ones — an extension's schema stays in
a database after it is switched off (deactivation is not a down-migration; the schema is additive and
harmless), so a report that only described active ones would stop naming an owner precisely when
someone is trying to work out where a leftover came from.

Two things make it fail, and the verb exits 1:

- **A divergence with no owner** — the effective parity policy allows a label, relationship type or
  superset property that no *active* extension declares. That means an extension's parity delta and its
  declarations have drifted apart, and the allowlist has grown an entry nobody can attribute.
- **An applied `ext/<id>/…` migration whose id this binary does not know** — the database carries
  schema from a module that is no longer registered. A downgrade, or a removed extension.

Live labels and relationship types are deliberately **not** scanned. On a shared database those belong
to other applications, and counting them would make the check impossible to pass there. This report
judges only shapes AgentMemory itself claims.

## How to write one

`ISchemaExtension` is **`internal` for the whole 1.x line**, and that is a deliberate limit rather than
an oversight: making it public would SemVer-lock a surface still being learned. Promotion to a
third-party extension point is a 2.0 decision. So this section describes how an extension is written
*in this repository* — it is not, today, a plugin API.

The smallest real example is [`procedural`](procedural.md), which declares one property and no
migration at all:

```csharp
internal sealed class ProceduralSchemaExtension : ISchemaExtension
{
    internal const string TraceKindProperty = "trace_kind";

    public string Id => "procedural";                 // lowercase-kebab, frozen on first ship
    public int Version => 1;

    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [SchemaConstants.NodeLabels.ReasoningTrace] =
                new HashSet<string>([TraceKindProperty], StringComparer.Ordinal),
        };

    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<string> MigrationScripts { get; } = [];              // legal: a properties-only extension
    public IReadOnlySet<string> BaseResidentMigrations { get; } =
        new HashSet<string>(["0011_trace_kind"], StringComparer.Ordinal);     // ownership of schema that predates this system

    public SchemaParityDelta ParityDelta { get; } =
        SchemaParityDelta.Create(addNetSupersetProperties: [TraceKindProperty]);

    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(StringComparer.Ordinal);
    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
```

The steps, and what each one is checked by:

1. **Implement `ISchemaExtension`** in `src/AgentMemory.Neo4j/Schema/Extensions/`. `Id` must match
   `^[a-z][a-z0-9-]*$` and `Version` must be ≥ 1 — both rejected at *startup* by the registry, because
   the id is a path segment and a `(:Migration).version` key.
2. **Declare every shape you write** in `DeclaredProperties` / `DeclaredRelationshipTypes` /
   `DeclaredLabels`. Prefer a property: a property is ungated by the parity verifier, while a label or
   a relationship type is parity-gated. Two extensions may not claim the same `(label, property)`,
   relationship type or label — every shape has exactly one owner, which is what makes the owners
   report answerable.
3. **Add the extension to `SchemaExtensionRegistry.CreateShipped()`** — the single list, read by DI
   registration *and* by every host-less caller (`schema-parity`, `schema-check`), so the two can never
   disagree about what exists. A reachability test reflects over the assembly and compares against what
   DI produces, so an implementation missing from that list is caught too.
4. **Write migrations, if any, at `Schema/Migrations/ext/<id>/000N_name.cypher`.** The
   `MigrationScripts` entry is the **bare filename** — the `ext/<id>/` prefix is supplied by the
   runner, and a value containing a slash is rejected. Every statement must be a
   `CREATE … IF NOT EXISTS` schema object or a `MATCH`-scoped backfill; `DROP`, `REMOVE`, `DELETE` and
   `DETACH DELETE` are refused by the lint. **Base migrations are exempt from that lint** — base is
   allowed to do things an optional module is not, and it is reviewed on those terms.
5. **Declare the parity delta**, or `SchemaParityDelta.Empty` when there genuinely is none
   ([`delta-recall`](delta-recall.md) is the shipped example of `Empty`, and its page argues why
   stating that explicitly beats leaving it as an absence). Adopting an upstream-only label — as
   [`working-memory`](working-memory.md) does with `:User` — is the **one legal overlap**, and it means
   pairing a `DeclaredLabels` entry with a `RemoveUpstreamOnlyLabels` entry. Without the pairing it is
   an undeclared label grab; with a stale one (a label the base policy does not list as upstream-only)
   the composition throws.
6. **Write `docs/extensions/<id>.md`.** This is enforced, not encouraged: a test drives itself from
   `CreateShipped()` and fails when the page is missing, when it lacks any of `## Shape`, `## Cypher`,
   `## Semantics`, `## Conformance`, `## Parity delta`, when a declared shape or migration filename is
   not named on the page, when a parity-delta entry is not named on the page, or when this index does
   not link it.
7. **Run the Gold-under-extension gate** — the treatment/control TCK diff described above.

Two shapes that look like mistakes and are not: an extension with **no migration script** (procedural —
its DDL is base-resident), and an extension with **no declarations at all** (delta-recall — it declares
seven indexes through its migration and nothing else, because an index is invisible to the parity
verifier by construction).
