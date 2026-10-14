# Module bookkeeping ownership

`module-bookkeeping` declares ownership for the experimental module migration ledger. It is off by
default. The module adapter and base schema reporting share this declaration through internal linked
source; the adapter does not access backend internals or require a friend-assembly grant.

## Shape

The SDK owns the `ModuleMigration` label and these properties:

- `module_id`, `key`: the composite ledger identity (`module_migration_key`).
- `sha256`, `state`: pinned script identity and its applying/applied state.
- `completed_statements`: number of fully committed statements within the pinned migration source.
- `actor`, `applied_actor`: explicit reserving and completing operator identities.
- `started_at`, `applied_at`: reservation and completion timestamps.
- `owner_module_id`: owner of a namespace claim held under the SDK ledger identity.

There are no declared relationship types. Namespace-claim keys belong to the SDK identity
`io.agentmemory.sdk`; module script keys belong to the actual module identity. This page declares
the owned shape used by explicit generated/handwritten application and interrupted-intent recovery.

## Cypher

This extension ships **no core migration scripts** and owns no base-resident migrations. Enabling
its schema reporting does not create a constraint, ledger row or `Migration` record. The held
`AgentMemory.Extensibility.Neo4j` adapter owns explicit module-ledger DDL and plan/apply.
Registration, schema reporting and module activation are separate operations.

## Semantics

A module ledger is separate from the base `Migration` ledger. Namespaces and script hashes must be
reserved before execution; interrupted application retains recoverable intent. Backfill data and its
`completed_statements` increment commit in the same transaction under the SDK migration lock. DDL
commits separately and replays through `IF NOT EXISTS` with exact shape checks. Completion is marked
only after all statements. Older applied generated rows without the counter remain readable; missing
handwritten progress is refused because replay could repeat a non-idempotent update. Enabling this schema
extension neither authorizes module code nor runs migrations. Empty default extension configuration
retains the existing effective base schema; no module label or query is introduced to that path.

For schema-owner reporting, request `module-bookkeeping` through `Neo4jOptions.Extensions` or the
CLI's existing extension selection. This selects declared schema metadata, not a background writer.

## Conformance

Ownership tests prove registry reachability, the unchanged default schema descriptor, explicit
owner reporting and the absence of core migration scripts. Generic extension tests validate declared
shapes, parity composition and this page. No TCK profile or database acceptance is
claimed for runtime activation. Actual generated-schema application/recovery tests
use a dedicated fresh Neo4j Testcontainer; they cover bootstrap-only application, repeat/roll-forward,
namespace/shape/hash refusal, crash recovery and competing ownership. Dedicated handwritten tests
cover mixed scripts, atomic backfill rollback, lost commit acknowledgements, concurrent matching
applies, later-statement failure/recovery, and altered source or missing-progress refusal.

## Parity delta

When selected, the declaration adds the .NET-only `ModuleMigration` label and the declared
`module_id`, `key`, `sha256`, `state`, `actor`, `applied_actor`, `started_at`, `applied_at` and
`owner_module_id`, `completed_statements` properties to the effective policy. No upstream-only labels are adopted and no
relationship types are added. The base descriptor and policy remain unchanged when it is off.
