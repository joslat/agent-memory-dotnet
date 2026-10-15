# Experimental module SDK

The current core release inventory holds the module SDK and its adapters on a separate experimental
version line. Source includes context contributors/compiler, metadata/profile resolution, explicit
module migrations, pure proposal admission and explicit ordered ingestion/replay. Transactional writes/reads and shared view evaluation are implemented in the held SDK/Neo4j adapter; explicit framework ingestion is wired; dynamic runtime activation remains unfinished. Registering contributors does not activate a module platform.

## Version and identity primitives

`AgentMemory.Extensibility.Modules.ModuleVersion.Parse` accepts complete SemVer 2 versions.
Comparison ignores build metadata, handles numeric prerelease identifiers numerically and supports
large numeric components without integer overflow. Value equality retains the full version spelling;
use `CompareTo` for precedence. See the [SemVer 2 specification](https://semver.org/spec/v2.0.0.html).

`ModuleVersionRange.Parse` supports NuGet-style exact `[v]`, bounded/unbounded intervals and bare
inclusive minimums. Numeric one/two-part endpoints are padded to three parts. Floating patterns,
malformed intervals and empty/reversed ranges fail. Prereleases are included when their SemVer
precedence lies inside the interval: `[0.1,0.2)` includes `0.2.0-preview.1` but excludes `0.2.0`.
The syntax follows [NuGet intervals](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning#version-ranges);
this SDK does not implement NuGet restore/version selection or floating-version behavior.

`ModuleIdentity` accepts lowercase dotted names with at least three segments, up to 64 characters.
Owned identifiers are `<moduleId>:<local>`; a local name starts with an ASCII letter and contains
letters, digits, dots, underscores or hyphens, up to 64 characters. The `io.agentmemory.` prefix is
reserved for project modules. Detecting that prefix is not publisher authorization; signing/trust
verification belongs to activation and is not implemented by the syntax validator.

All these public types carry `AMEXT001` while the module API is experimental.

## Shared dependency ordering

Built-in schema extensions and the module SDK link the same internal dependency-order source.
It expands only registered dependencies and returns dependencies before dependents, preserving
the built-in ordinal batches of ready nodes. Missing dependencies and cycles fail through each
caller's diagnostics. An empty root set does not read dependency declarations. No runtime assembly
dependency on Neo4j is introduced into the SDK, and no public registry API is added.

Built-in activation and its error messages remain unchanged. The shared primitive does not discover
packages, activate module code, execute DDL, or make a complete module set available by itself.

## Manifest wire reader

`ModuleManifest.Parse(json)` reads `agentmemory.module/v1alpha1` without loading an assembly,
executing a contributor, or accessing storage. Input is capped at 128 KiB of UTF-8 and JSON depth 32.
It rejects duplicate/unknown fields, comments, trailing commas, explicit nulls where a value is
expected, malformed scalar declarations and retired `routing` fields. Errors include the JSON path.

The typed result carries dependency intervals, explicit History/Reminders requirements, ontology
properties/relations and declarative views. Parsed collections are read-only; numeric bounds and
filter literals are cloned from the parser document, retaining their JSON numeric precision.
Views support the short ascending sort or the explicit property/descending form. Optional property
and relation descriptions follow the 200-character bound. Shared scope is declaration data; parsing
it does not authorize a shared write.

This reader validates wire shape, not complete semantic compatibility. Call `Validate()` for the local
ontology and view checks below. The pure resolver below checks complete metadata sets; profile/DI, trust and activation remain unfinished. Do not treat a successful parse as
permission to execute code or apply schema. No existing host activates manifests automatically.

## Assembly discovery without implementation execution

Use `ModuleManifest.LoadFromAssemblyFile(path)` or `LoadFromAssemblyImage(bytes)` before loading an
implementation assembly. Both inspect managed PE metadata, require exactly one embedded
`*.agentmemory-module.json`, and compare its version to the single assembly informational-version
attribute. SemVer build metadata is ignored for this comparison; prerelease identity is not.
Discovery does not load implementation dependencies, instantiate attributes or run initializers.
It rejects linked external manifests, malformed resource bounds and invalid UTF-8. Assembly images
are capped at 64 MiB and the embedded manifest retains the 128 KiB limit.

`ModuleManifest.Load(assembly)` supports an assembly the caller already loaded; it reads attribute
metadata without constructing attributes. It cannot undo code the caller has already executed.
For untrusted implementation files, use the file/image overloads before any runtime loading.

Discovery is not publisher verification or a sandbox. A later activation path must verify and load
the authorized bytes; parsing a path once does not authorize whatever later occupies that path.
Profile/DI validation, signature checks and runtime activation remain separate unfinished work.
The implementation uses the framework's [PE metadata reader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.metadatareader)
and [PEReader](https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader).

## Local semantic validation

`manifest.Validate()` returns a read-only list of all local problems in ordinal order. It also checks
manifests constructed in C#, including malformed collections; a successful JSON parse is not required.
It performs no database work, code execution or provider calls. It checks:

- Dotted module/dependency identities, required version/range objects, supported explicit host
  features, owner/shared scope and duplicate type, relationship and view names.
- PascalCase label prefixes and owned type names; relationship names use the prefix's UPPER_SNAKE
  form followed by an underscore and suffix. Schema-name normalization cannot collapse two labels.
- Reserved core/upstream/built-in labels and relationships, SDK provenance properties, and migration/
  key bookkeeping labels. Repository tests pin the vocabulary to actual core and built-in declarations.
- Property kinds, nonempty unique string enumerations, string-length and numeric constraints.
  Integer bounds must fit Int64 and are compared as integers, preserving large-value distinctions;
  float bounds must be finite. Property/relation descriptions have 1-200 characters when supplied.
- Render placeholders name declared properties; doubled braces escape literal braces. Relationship
  sources are own types; targets are own types, `core:Entity`, or a qualified declared dependency.
- Views name own types and declared filter/sort properties. Limits are positive; predicates have
  compatible literals or `$now` (datetime) / `$session` (string). Membership takes a typed array.
  Boolean ordering is refused; null is permitted only with equality/inequality. Dates use yyyy-MM-dd;
  datetime literals must carry an explicit ISO 8601 offset. No query text is accepted as a predicate.

Local validation does not establish that a dependency's referenced type exists, that its version
matches, that two modules have disjoint namespaces, or that the host supports required capabilities.
Those require complete-set resolution. Signature verification and activation remain separate.
The editor schemas and generated schema-object guards below complement this validation;
actual host composition and activation remain separate obligations.


## Complete metadata sets

`ModuleResolver.Resolve(manifests, sdkVersion, availableFeatures)` deep-freezes declarations and
returns dependency-ordered `Modules`, ordinal `Errors` and a `ManifestFingerprint`. Any error returns
an empty module set and null fingerprint. Duplicate identities are rejected even when one declaration
is locally invalid. Missing/invalid dependencies, incompatible ranges, SDK requirements and independent
cycles are diagnosed together. The resolver checks cross-module prefix/label/relationship collisions
and that declared dependency targets actually exist. Ontology/views imply `ModuleSchema`.

Caller arrays, dictionaries and JSON documents may be changed/disposed after resolution without
changing the result; inputs must not be concurrently modified during the call. The fingerprint is
SHA-256 over normalized metadata and the target SDK, with ordinal maps/module order. Named arrays
retain their declaration order. It is not the eventual active-set fingerprint: composition still
needs profile, configuration, migration and trust identities.

Available features are explicit planning inputs, not proof of a running host. A host must derive them
from actual effective registrations/options. This API performs no DI, provider calls, module code
execution or schema work, and does not authorize activation. Profile registration reconciliation,
actual host probes and runtime activation remain unfinished.


## Composition profiles

`ModuleCompositionProfile.Parse(json)` reads `agentmemory.modules/v1alpha1` with the same strict
JSON helpers as manifests: 128 KiB UTF-8 limit, depth 32, duplicate/unknown field refusal and no
source loading. A profile declares `stores` (alias to `provider`) and `modules` (id, version interval,
store, exactly one `assembly` or `manifest`, optional `configuration` and optional `sha256` bundle pin).
It preserves an exact UTF-8 input hash, including whitespace. Collections are immutable.

`ModuleProfileResolver.Resolve(profile, registrations, sdkVersion, availableFeatures, storeProviders)`
uses the metadata resolver, then checks missing/extra module registrations, profile version intervals,
store aliases and provider identities. Declared graph links across different store aliases are refused;
non-graph dependencies can still span stores. A failure exposes no composed modules or fingerprint.
An omitted options section resolves to `AgentMemory:Modules:<moduleId>`; no values are bound here.

The composition fingerprint includes metadata and exact profile bytes. Host/CLI may use this pure
result to prepare activation, but must still discover the declared source bytes, verify trust/pins,
derive actual host capabilities, bind and validate options, and include those identities in the
active-set fingerprint. A parsed path or digest never authorizes loading it. This checkpoint provides
no profile DI extension, loader, CLI execution or permanent ModulePlan singleton. DI-only metadata
composition remains available through `ModuleResolver`; running-module composition remains unfinished.


## Editor schemas and generated object names

Standalone draft 2020-12 schemas are included in the SDK package under `contentFiles/any/any/Schemas/`:
[manifest schema](../../src/AgentMemory.Extensibility/Schemas/agentmemory.module.v1alpha1.schema.json)
and [profile schema](../../src/AgentMemory.Extensibility/Schemas/agentmemory.modules.v1alpha1.schema.json).
Associate them through editor settings; the strict runtime readers do not accept an added `$schema`
field in module/profile documents. Schema validation is guidance, not a substitute for C# validation.
Duplicate JSON fields, byte/depth limits, cross-field bounds/order, namespace ownership, references,
render placeholders and actual host/trust conditions remain runtime checks. The schemas have no
external schema references and need no application network access. Runtime validation has no new
library dependency; schema conformance uses a test-only validator.

`ModuleSchemaObjects.Describe(manifest)` produces immutable sorted data descriptors, without Cypher
or database access. A type has `<snake_label>_id` uniqueness, `<snake_label>_owner_id_idx` and
`<snake_label>_key_id_idx` range indexes, plus `<snake_label>_<snake_property>_idx` for each indexed
property. Descriptors retain exact label/property spellings; normalization applies to names only.

Local validation rejects collisions within a module and with reserved base, built-in, upstream and
SDK bookkeeping objects. Complete-set resolution also rejects collisions across modules, even when
labels and prefixes differ. Repository tests pin reservations to actual bootstrap builders, shipped
migrations and the upstream snapshot. These are planned shapes: migration ownership, existing-object
checks, DDL application and the migration ledger are provided by the held storage adapter below;
this portable descriptor itself performs no storage work.


## Neo4j migration definitions

The experimental `AgentMemory.Extensibility.Neo4j` storage adapter is held at version 0.1.0.
Its internal definition builder consumes successful frozen complete-set resolution: dependencies
first, then derived object names in ordinal order. Each object has a stable `derived/<object-name>`
key, quoted identifiers, idempotent constraint/index creation text and lowercase SHA-256 over
UTF-8 text with CRLF/CR normalized to LF. Exact property spelling is preserved. Adding a type
adds definitions without changing existing keys or hashes. Empty composition produces no work.

Built-in additive lint is linked into the adapter for hand-script validation. Generated definitions
use fixed templates and validated identifiers: quoted properties such as `delete` are valid and
must not be misclassified as destructive operations by a script-text regex. The shared lint is
not an arbitrary Cypher security sandbox. Embedded source discovery, namespace checks and explicit
execution are available below; hostless CLI integration remains unfinished. No migration runs during
registration. The portable SDK remains independent of Neo4j.

## Explicit module schema application

`new ModuleMigrator(transactionRunner, resolution)` prepares generated definitions for one store,
without executing module code or database queries. `PlanAsync()` reads the existing schema and
ledger. `ApplyAsync("operator-or-host-name")` explicitly reserves ownership and immutable hashes,
applies pending DDL and stamps completion. Both accept cancellation. A composition with no schema
claims performs zero database calls; reapplying a completed set performs no DDL or ledger writes.

Base bootstrap constraint names must be present; running base migrations is not required. The SDK
uses a separate `ModuleMigration` ledger with composite uniqueness on `(module_id, key)`. Namespace
claims (prefix, label, relationship and object name) live under `io.agentmemory.sdk`; script intents
live under each module ID. An application module cannot use the exact SDK bookkeeping identity.
A shared transaction lock serializes reservation checks across compliant
hosts. All pending hashes and claims commit before any module statement. DDL and progress stamps
use separate transactions: matching retries use `IF NOT EXISTS` plus exact shape checks. Each backfill
and its `completed_statements` increment share one transaction under the same SDK lock, so an
increment is not repeated after a lost commit acknowledgement or by a concurrent matching apply.
The migration remains `applying` until all statements complete.
Foreign ownership, mutated hashes, changed/missing completed shapes, failed indexes and equivalent
objects under another name are refused. Invalid or absent handwritten progress is refused; older
generated ledger rows without progress remain readable because each contains one replay-safe DDL statement.
Previously applied keys omitted by an upgrade are reported and retained. A relationship-only upgrade
still reserves its namespace even when no new index is required.

To include explicit handwritten sources, use the four-argument overload. It preserves the existing
three-argument constructor, including calls that pass a null time provider:

```csharp
var scripts = ModuleMigrationScripts.LoadFromAssemblyImage(authorizedImage);
var migrator = new ModuleMigrator(transactionRunner, resolution, scripts, TimeProvider.System);
var plan = await migrator.PlanAsync(cancellationToken);
// The operator/host explicitly chooses to apply this authorized source and composition.
var result = await migrator.ApplyAsync("operator-name", cancellationToken);
```

The caller authorizes the source bytes and metadata. Source loading does not execute implementation
code. Explicit CLI plan/apply is available; runtime activation remains unfinished; explicit record writes check migration readiness. Administrators must serialize external schema changes with application;
the reservation lock coordinates compliant module migrators, not arbitrary administrative DDL.
For schema-owner reporting, select the optional [`module-bookkeeping`](module-bookkeeping.md)
declaration. Neither its registration nor the migrator constructor changes the base schema.


## Embedded migration source discovery

`ModuleMigrationScripts.LoadFromAssemblyImage(image)` reads definitions from PE metadata without
loading the assembly, running its initializer or constructing attributes. It first validates the
embedded module manifest and informational version through the portable SDK. It accepts default
MSBuild `*.Migrations.000N_name.cypher` resources and explicit root `Migrations/000N_name.cypher`
logical names. Scripts return in ordinal key order with the manifest's module ID and a hash over
the complete source, including comments, with only line endings normalized to LF.

Sources must be UTF-8 without a BOM. Limits are 256 KiB per script, 1 MiB combined and 128 scripts
per assembly, within the existing 64 MiB image limit. Linked files, duplicate keys, malformed names,
invalid UTF-8, empty scripts and incomplete quoted expressions are rejected. The bounded lexical
reader preserves quoted semicolons and URLs, removes comments without joining tokens, and rejects
unclosed or excessively nested input. Backslash-escaped identifiers are deliberately rejected;
doubled backticks are supported. Parsing is not complete Cypher grammar or authorization validation.

The internal compiler now extracts exact named range-index and uniqueness-constraint shapes,
including ordered composite properties and node/relationship identity. It requires `IF NOT EXISTS`,
static identifiers and matching property variables. It also accepts bounded `MATCH ... [WHERE ...]
SET ...` backfills: each node has an explicit owned label, each relationship an explicit owned type,
and property writes target a bound variable. Scalar expressions support property reads, literals,
lists, arithmetic, comparisons, Boolean operators and a fixed set of built-in conversion/string/date
functions. Procedures, user functions, subqueries, dynamic access, variable-length paths, rebinding
clauses and graph deletion are rejected. This is a deliberately explicit grammar, not unrestricted Cypher.

`ModuleNamespaceReferences.Validate` shares the manifest validator's naming and reserved-name rules.
Historical names within the owned prefix need not occur in the current ontology. SDK-managed property
writes are refused; reading or indexing those properties is allowed. The existing additive lint is
shared without changing built-in migrations; quoted values and comments are not interpreted as commands.

Compilation checks full-source hashes and resource limits, orders generated definitions before scripts
within dependency order, and collects historical label/type/object claims. Conflicting module ownership,
duplicate object names and equivalent shapes under another name are rejected before database access.
The compiler has unit coverage; database shape assessment and ownership reservation remain separate checks.

Discovery and compilation do not authorize execution. Explicit script-input application uses the same
compiled shapes, ownership claims and ordered statements as planning. Real Neo4j acceptance covers
mixed composite/relationship schema and backfills, matching recovery after DDL or lost commit
acknowledgements, rollback, concurrent matching applies, later-statement failure and altered input
refusal. Base migration rows remain unchanged. Explicit CLI plan/apply uses this same engine; runtime activation is still unfinished.


## Operator CLI for module migrations

The non-packable operator CLI accepts the experimental composition profile:

```sh
dotnet run --project tools/AgentMemory.Cli -- modules plan --profile modules.json --store local
dotnet run --project tools/AgentMemory.Cli -- modules apply --profile modules.json --store local --actor release-operator
```

Use the ordinary `--uri`, `--user`, `--password`, `--database` options or matching Neo4j configuration
and environment values to select the connection. One invocation addresses one store/database;
multiple store aliases require an explicit `--store`. Only `neo4j-direct` providers are supported.
Plan reads schema/ledger without writing. Apply reserves and applies the same compiled migrations.
Exit codes: 0 for a successful plan/application, 2 for a database-plan refusal, 1 for invalid input
or execution failure. Standard output is a JSON report; failures are written to standard error.
Unknown options are rejected, so an unsupported `--dry-run` cannot silently become an application.

```json
{
  "format": "agentmemory.modules/v1alpha1",
  "stores": { "local": { "provider": "neo4j-direct" } },
  "modules": [
    { "id": "com.example.reviews", "version": "[0.1,0.2)", "store": "local",
      "assembly": "modules/Reviews.dll" }
  ]
}
```

An entry can use `manifest` instead of `assembly` for generated schema only. Paths are resolved
relative to the profile file. Optional `sha256` pins cover exact source bytes, including assembly
metadata/resources. All sources are read into bounded snapshots before execution; an assembly's
manifest and embedded scripts come from the same image. Profile/manifest JSON is limited to 128 KiB,
assembly images to 64 MiB. Strict UTF-8 decoding prevents replacement-character identity changes.
The report includes exact profile/source hashes, composition/store fingerprints, module versions,
normalized migration hashes, pending/applied definitions, retained undeclared keys and refusal reasons.
A pin proves byte identity, not publisher trust. This command does not activate module code.

The whole profile must resolve successfully before the selected store is projected. Dependencies
in another store remain valid composition dependencies; cross-store graph relationships are refused.
Selecting an empty store cannot hide an invalid dependency elsewhere in the profile. A valid empty
store makes no migration database calls.

The tool supplies `ModuleSchema`. Modules requiring `History` or `Reminders` need explicit
`--host-features History,Reminders` target declarations. These declarations are reported as such:
they are not probes of live host services, options validation or proof of runtime activation.
The held SDK references are confined to this non-packable tool; published core dependencies remain unchanged.


## Schema preview without a database

`ModuleSchemaDerivation.Derive(manifest)` returns the generated DDL definitions, stable keys and
normalized hashes using exactly the same derivation as the migrator. This experimental public API
lets editors and DemoBrain preview a locally valid manifest without opening a database, activating
a module or inventing dependency registrations. Code-only modules return no definitions.

Local preview is not whole-profile validation or application permission: dependency compatibility,
namespace collisions across modules, host capabilities, source trust and database ownership must
still be checked before applying. Handwritten scripts remain separate explicit inputs.

## Typed proposals and pure admission

`AgentMemory.Extensibility.Ingestion` defines `IngestionRequest`, `IngestedMessage`,
`IngestionProviderDescriptor`, `ModuleProposal`, `ProposedLink` and `ProposalSource`.
These are model-independent, JSON-serializable inputs; a proposal cannot supply its own module or owner.
The host must resolve the claimed owner through its isolation policy before exposing it to providers.

`ModuleProposalAdmission.Admit(resolution, provider, request, proposal)` uses the frozen metadata
resolution. It returns concrete errors or an independent immutable proposal snapshot. It validates:

- Provider ownership, declared source type and a nonempty logical key without control characters.
- Declared scalar properties, required fields, exact case-sensitive enums, string length and inclusive
  numeric bounds. Integer checks retain full Int64 precision. Optional absent properties are omitted;
  explicit null is refused. Date/time values are literals: `$now` is not a valid timestamp proposal.
- Source IDs belonging to the request, without duplicates, plus a nonempty excerpt of at most 500
  UTF-16 code units. The accepted snapshot survives disposal of the input JSON document.
- Declared outgoing relations, distinct targets, proposed cardinality and target selectors. Exact
  entity-name/alias lookup is permitted only for `core:Entity`; lookup itself belongs to storage.
- Shared types only for an `OnRequest` provider explicitly selected by an `OnRequest` host request.
  Selecting an `EveryTurn` provider explicitly does not grant it permission to write shared records.
- `Invalidate` operations carry identity and provenance, with no silently ignored property/link changes.

**Admission is a pure shape check, not a write or proof of persisted provenance.** The concrete store
verifies source messages against the resolved owner's session, migration readiness and scoped/live
targets in its transaction. It checks required anchors and final cardinality after merging retained
links; omitting a required link from an update does not establish that the final record lacks that link.
Text remains untrusted and must pass the host's existing rendering policy. No DI activation, model
call, storage query or existing prompt change is introduced by these types and helper.

## Ordered ingestion and recorded replay

`ModuleIngestion` implements `IModuleIngestion`. A host constructs it with a successful frozen
`ModuleResolution`, authorized `IIngestionProvider` instances, explicit module-ID-to-`IModuleStore`
bindings, its `IMemoryIsolationPolicy`, a `TimeProvider` and logger. This is explicit composition;
`AddAgentMemoryExtensibility()` does not activate or register a module ingestion engine automatically.

Each call resolves the claimed owner with tenant access **before any provider runs**, then freezes
source messages and provider selection. An owner-policy refusal returns `Denied` without invoking a
provider, store or clock. An empty provider set performs no policy, clock or store work. Automatic
turns skip OnRequest providers; explicit requests reject unknown or duplicate selected IDs.

Providers run in metadata dependency order, then ordinal provider-ID order within a module. Each
admitted batch is awaited before the next provider runs. Shape refusals retain original output indices;
accepted proposals make one store call. Empty/all-refused batches make none. Different provider batches
and core persistence are separate transactions. A failed provider does not erase earlier results or
prevent later providers from using already-existing records. No retry or exactly-once guarantee is added.

`IngestionReport.Recorded` retains frozen, serializable proposal batches before admission, including
shapes later refused and batches whose store call failed. A batch binds its provider/trigger, metadata
fingerprint, canonical resolved-request hash and captured write clock. `ReplayAsync` rechecks owner
policy and validates **all** recordings before writing the first batch. It runs admission/storage in
dependency order with the recorded clock, without calling providers or the current clock. The request
hash includes owner, session, messages, turn time and explicit selection. Hashes bind inputs but are not
signatures or permission grants; replay is a trusted host operation. Reproducing a result requires a
matching initial graph, and this is not replay of core extraction or an entire agent conversation.

Provider and store failures are reported separately. A store exception or malformed receipt produces
`Unavailable` with **unknown commit status**, since losing an acknowledgement does not prove rollback.
Store receipts must cover each admitted index exactly once, use supported outcomes and carry reasons
only for refusals. Cancellation propagates, including when a provider ignores cancellation and returns
an empty result. Raw exception text is logged through the host logger, not copied into the report.

`IModuleStore` defines the transactional write contract; the held Neo4j adapter implements it below.
Orchestration tests use controlled providers/stores to prove execution behavior. Separate fresh-Neo4j
tests exercise actual write invariants. Explicit framework persistence callbacks, scoped reads and shared views are available below. Neither suite is a coding-memory effectiveness benchmark.

## Transactional Neo4j module writes

`AgentMemory.Extensibility.Neo4j.Storage.Neo4jModuleStore` binds a public transaction runner, frozen
module resolution and any explicit handwritten migration sources. It is constructed explicitly; no
service is automatically registered. Source/metadata/provider mismatches are refused before writes.

`ModuleMigrator.ApplyAsync` now also prepares the SDK-owned `module_key_id` uniqueness constraint.
`PlanAsync` reports missing SDK constraints in `PendingBookkeeping`; empty compositions still make no
database calls. Existing objects with the wrong shape or an equivalent foreign name are refused.
The optional [module-bookkeeping](module-bookkeeping.md) declaration owns the key fields; core
bootstrap and the base `Migration` ledger remain unchanged. A record write never applies DDL.

Within one transaction the store:

1. Reassesses the configured migrations and refuses pending/inconsistent work, including a recorded
   applying migration omitted from its configured source list.
2. Verifies each cited message through its actual `Conversation`/`HAS_MESSAGE` relationship, matching
   the resolved owner, session, stored conversation ID, role and content. Request IDs alone are insufficient.
3. Locks distinct logical keys in stable order. The unique `ModuleKey` hashes module/type/owner/key;
   its version counter and current reference govern deterministic version IDs. A transient lock marker
   is set and removed before reading both properties. Concurrent hosts cannot create two current versions.
4. Validates current-record identity/owner and resolves declared links inside the transaction. Core
   IDs or exact names/aliases can follow `merged_into`; module keys and carried links follow
   `superseded_by`. Chases stop after 16 records or a cycle; name lookup refuses over 64 candidates.
   Ambiguous, missing, out-of-scope or invalidated proposed targets are refused. Shared source records
   can link only to shared targets. Required anchors and final cardinality are checked after copying links;
   an explicit max-one link replaces its copied predecessor.
5. Returns `NoOp` for equal properties and links; otherwise appends a version, stamps the old record
   and connects `SUPERSEDED_BY`. Invalidation preserves the record and clears the key's current reference.
   A write clock before the preceding creation/invalidation is refused rather than creating overlapping
   history. Providers must order proposals when one references a record created earlier in the same batch.

A transactional invariant refusal rolls back the whole admitted provider batch, including earlier
proposals and newly created key rows. Unexpected errors propagate, preserving unknown-commit handling
in the orchestrator. Historical records retain copied source IDs/session/excerpt when messages are
cleared. Core records are not rewritten or relabeled. Framework callbacks are explicitly wired; dynamic runtime activation remains unfinished;
these write APIs alone do not complete end-to-end module ingestion.

## Scoped module reads and shared views

The experimental `IModuleReader` is a read capability separate from `IModuleStore` write authority.
Construct `Neo4jModuleReader` explicitly with the resolved set, calling-module ID, real host isolation
policy, transaction runner and any configured handwritten migration sources. Construction performs no
query and registers nothing. The host grants the bound reader; a query cannot change its caller identity.

`FindAsync(ModuleQuery, ModuleReadContext, cancellationToken)` accepts only owned types in the calling
module or its direct declared dependencies. The host isolation policy resolves owner/shared visibility
before database work. Pending/inconsistent migrations refuse reads; reads never apply schema or write
records. Queries use the manifest's closed typed predicate language, with parameterized values and
validated identifiers. Limits are 1-1000 records, at most 64 predicates and 256 membership values.

Current queries return live versions; `SystemAsOfUtc` selects the half-open creation/invalidation
interval. `$now` uses that pin, or the injected clock for current reads; `$session` requires an explicit
session. `ValidAsOfUtc` returns `NotApplicable`, not today's records. `Succeeded` with no records means
searched-and-empty; `Refused` means invalid scope/query/readiness. Dates and datetimes use native Neo4j
typed comparisons; optional null equality/inequality uses missing-property semantics. Sorts place nulls
last, respect direction, and use version ID for ties.

Declared outgoing links resolve only within the record's allowed owner/shared scope. Identity chasing
is bounded to 16 records and stops on cycles. System-time reads retain the pre-merge target where it
was live. Missing required anchors exclude records **before** applying the result limit. Paging is
cancellable; the requested result limit does not imply a fixed scan-cost bound or a concurrent snapshot.

`IModuleViews`, implemented by `ModuleViews(resolution, reader)`, evaluates a declared local view through
that same reader and returns ordered `ModuleViewItem` values: exact stored records, copied provenance
and rendered text. It does not perform admission or declare rendered text safe for HTML/prompts.
The explicitly registered `ViewContributor` uses this same service. Conformance-kit and Workbench
wiring remain separate work. No existing host automatically activates it.

For a view selected by a specific entity or logical key, call
`EvaluateWithArgumentsAsync(viewId, ModuleViewArguments, context, cancellationToken)`. Arguments
can supply an exact `Key`, conjunctive `Links` and a smaller positive `Limit`. They cannot replace
the declared type, property predicates or ordering, or increase the view's limit. Older implementations
of `IModuleViews` refuse this new entry point by default; they never silently ignore restrictions.
The original `EvaluateAsync` entry point keeps the same query and scope behavior.

```csharp
var result = await views.EvaluateWithArgumentsAsync("open", new ModuleViewArguments
{
    Links = [new ModuleLinkFilter { Relation = "COMMITMENT_ABOUT", TargetId = entityId }],
    Limit = 5
}, new ModuleReadContext { Scope = MemoryScope.For(ownerId) }, cancellationToken);
```

The example assumes the `open` view declares a limit of at least five and its source type declares
`COMMITMENT_ABOUT`. Direct `ModuleQuery` reads accept the same `Links`. Up to 64 link predicates
are allowed; target IDs must be nonempty and at most 1024 characters. Each relation must belong to
the queried source type. Predicates are ANDed, captured independently of caller-owned collections,
and evaluated against resolved outgoing links **before** the result limit, including across storage
pages. Target IDs are data, not names or Cypher. Both the supplied identity and stored link follow
the same bounded merge/supersession rules, record-owner/shared scope and system-time pin. A missing,
foreign or unresolved target yields no matching records; malformed predicates refuse the query.
To list retained versions, set `ModuleQuery.Selection = ModuleRecordSelection.History` and supply
one exact `Key`. A declared view uses `"selection": "history"`, with its key supplied through runtime
arguments. A missing key refuses; arguments cannot change a view's declared selection. Snapshot
selection remains the default, including when `"selection": "snapshot"` is explicit; existing manifest
fingerprints are unchanged by the default.

History returns retained versions, including superseded/invalidated and zero-duration versions, with
the same 1-1000 record limit. It orders by creation time descending, numeric SDK version suffix
descending (so version 10 precedes 9), then identity ascending. Property ordering is refused for
history. An optional `SystemAsOfUtc` is an inclusive **creation cutoff**, while required anchors and
link filters use that same query-time pin. Without a pin, they require current targets. A deleted or
foreign anchor is not resurrected by history. `ValidAsOfUtc` remains unsupported.

History is a retained version listing, not a historical database snapshot: `InvalidatedAtUtc` keeps
its stored audit value even when that invalidation happened after the creation cutoff. Copied source
provenance remains available after source-message deletion. No versioning flag, schema mutation or
write behavior changes are introduced by selecting history.

The five-argument `ViewContributor` constructor accepts a cheap
`Func<ContextRequest, ModuleViewArguments?>` after the explicit priority. It runs once per contribution,
after the compiler resolves the owner, and cannot replace that resolved owner/shared scope. A null
selection produces `NotApplicable` without reading storage; invalid arguments produce `Denied`.
Unsupported valid-time requests never invoke the selector. The original constructor still uses the
original view call. This lets a package select an entity or history key without copying contribution,
rendering or isolation logic. Selecting arguments is host/package domain logic, not an automatic router.

Rendering shares the manifest's placeholder grammar: `{property}`, optional .NET scalar format,
and escaped `{{`/`}}`. Numeric/date formats use invariant culture; datetimes normalize to UTC. Stored
braces are data, not recursively expanded templates. Missing optional values render empty; missing
required values or invalid formats refuse the view without returning partial items. Without a template,
only present declared properties render, in ordinal property-name order as `name=value` pairs.
The host remains responsible for admission, output budgets and handling full-trust module code.

Isolated integration tests exercise actual writes followed by current/historical reads, strict/shared
scope, declared dependencies, required-anchor loss, merge identity, native typed filters, descending
view order and rendering. These are storage/view contract checks, not completed runtime activation,
dynamic module loading or a measure of memory usefulness.

## Persisted-turn adapter seam

`Neo4jMemoryContextProvider` exposes a protected opt-in `ObservesPersistedTurns` and
`OnTurnPersistedAsync(PersistedMemoryTurn, CancellationToken)` callback. The default is false:
no callback, extra clock read or source projection occurs. It is independent of core
`AutoExtractOnPersist` and `ExtractFromUserMessagesOnly` settings.

The callback runs after all turn messages have been successfully stored and core extraction has
completed or been queued. Its immutable input is captured before core extraction filters the roles.
It receives storage-returned IDs/content/roles, a read-only source list, session/conversation, host
owner claim and captured application ID. Mutable message metadata and embeddings are not exposed.
This is not an owner authorization grant or a multi-message transaction receipt. A later storage
failure can leave earlier messages persisted, but does not publish a successful-turn callback.
Invocation failures and empty turns publish nothing. Non-cancellation observer failures are logged
without undoing core extraction; caller cancellation propagates.

The callback itself neither activates module ingestion nor schedules background work. A derived adapter
must not reconstruct source IDs or save the turn a second time.


## Explicit Agent Framework module ingestion and views

Call `AddAgentMemoryFrameworkModuleIngestion(createIngestion)` after the core memory and Agent
Framework registrations to enable automatic module ingestion. The factory receives the active scoped
service provider and must return an `IModuleIngestion` bound to an authorized, fixed module resolution,
providers and stores. Registering `IModuleIngestion` alone or calling
`AddAgentMemoryFrameworkExtensibility()` does **not** enable automatic module writes.
Without explicit activation there is no module callback, source projection or pipeline resolution.

The handler passes actual stored user **and** assistant sources as `EveryTurn`; it does not inherit
core extraction's user-only filter. It neither invokes `OnRequest` providers nor grants shared writes.
The real pipeline still resolves isolation before generation and verifies provenance against storage.
Provider refusals/unavailability are logged by provider identity/status without logging source text.
Non-cancellation callback failures leave core extraction operational; cancellation propagates.

`AgentFrameworkOptions.ExtractInBackground` also selects module dispatch. Background work uses the
existing `IBackgroundExtraction` queue, ordered by owner (or session when unowned), after core work for that key. It captures immutable
sources and the binding, creates a fresh DI scope, restores the captured application/owner, and only
then constructs the pipeline. A missing/unrestorable application scope refuses ingestion. Rejected
queue submissions drain earlier work for that key before falling back inline. Hosts should drain the
queue before disposing storage. This is in-process scheduling, not durable or exactly-once delivery.
Runtime-set leases, dynamic replacement and unload safety are separate capabilities; a static factory
must not resolve an independently changing module set and claim snapshot consistency.

Register a `ViewContributor(resolution, reader, viewId)` as `IContextContributor` to expose a declared
view. It uses `ModuleViews` rather than duplicating predicates/rendering, carries exact version IDs
and provenance in section metadata, and preserves the compiler's resolved owner/shared scope.
`ContributeResultAsync` distinguishes refused, inapplicable and searched-empty results. Its default
interface implementation preserves existing contributors' section-only behavior. The native provider
continues to apply the core recalled-memory admission policy to rendered module text.


Owned ingestion requires a conversation already associated with that owner and session. The trusted
host should create it through `IShortTermMemoryService.AddConversationAsync(conversationId, sessionId,
userId)` in the selected application scope before the first turn. The adapter's minimal conversation
creation does not assign ownership. `WithMemoryIdentity` supplies a claim; it cannot claim an existing
unowned/foreign conversation. Module storage refuses mismatched source ownership and never changes
core conversation ownership to make ingestion succeed. Use host-authorized conversation identities;
`AddConversationAsync` is an administrative upsert, not a tenant-claim operation.

### Native session read times

The experimental Agent Framework adapter accepts host-selected module times through
`session.WithModuleReadTime(systemAsOfUtc: instant, validAsOfUtc: null)` in
`AgentMemory.Extensibility.AgentFramework`. Both arguments replace the previous selections; call
`WithModuleReadTime()` to clear them. UTC round-trip strings survive framework session serialization.
The adapter passes them to `ContextRequest.SystemAsOf` and `AsOf` before compiling contributors.
Unsupported valid-time reads retain the shared reader's NotApplicable result.

These selections affect module retrieval only: core recall, source persistence and module write times
are unchanged. A malformed saved selection refuses module compilation (with the existing warning)
while preserving core context; it never silently substitutes current module data. With no registered
module contributors, the adapter does not inspect these state keys. The host must separately configure
core history/recall if it needs a wholly historical context or a module-only acceptance control.

## Effective host capability checks

`ModuleHostProfileResolver.Resolve` checks a structurally valid profile against actual host service
bindings. Supply a `ModuleHostStore` per alias, with the effective service provider, provider identity
and optional service key. These are trusted host inputs, not module declarations. Structural failures
return before probing services or evaluating options; any capability failure returns no partial module
set and no composition fingerprint.

| Requirement | Evidence inspected in the bound store |
|---|---|
| History | Registered `IMemoryHistoryService` |
| Reminders | Effective `IOptions<MemoryOptions>.Value.Recall`: `ProspectiveFiring=true` and `ValidTime=Current` |
| ModuleSchema (derived from ontology/views) | Registered `IModuleStore`, plus a matching storage adapter probe; `Neo4jModuleSchemaCapabilityProbe` additionally requires `INeo4jTransactionRunner` |

A keyed store must supply these services/options at that exact key. Nothing falls back to unkeyed
registrations or borrows capabilities from another store. Dedicated store containers may use unkeyed
bindings. If a container cannot inspect registrations through `IServiceProviderIsService` or
`IServiceProviderIsKeyedService`, a required capability is refused rather than guessed. Missing or
invalid reminder options also refuse that requirement. Host options validation details are not copied
into module diagnostics.

The Neo4j probe lives in `AgentMemory.Extensibility.Neo4j`; the SDK has no Neo4j/Core dependency.
The SDK references `Microsoft.Extensions.Options` to read effective options, without installing a
configuration provider. NAMS registrations alone cannot provide the Neo4j module-schema capability.
The probe does not construct module stores, history services or runners, open connections, run DDL,
load implementation assemblies or call models. Trusted options factories and adapter probes may run.

Capability is not readiness: successful registration inspection does not prove that factories can
construct services, the database is reachable, migrations are applied or a publisher is trusted.
The returned fingerprint still describes metadata only. Runtime option binding, immutable activation,
leases and load/unload remain separate work. Existing `ModuleProfileResolver` and CLI `--host-features`
remain pure planning surfaces; their feature claims are not runtime capability evidence. No probe is
called by existing hosts unless they explicitly use this API.

## Independent Commitments package example

[Commitments & Follow-ups](../../eng/module-consumers/Commitments/README.md) is an example authoring
consumer, built entirely against freshly packed NuGet SDK packages. It supplies owner-scoped
CommitmentItem records, required COMMITMENT_ABOUT anchors, structured user teaching, and entity/due/
history contributors using the shared views. V2 adds optional indexed priority_reason; apply its
owned schema change explicitly and do not invent a backfilled reason.

The [consumer verifier](../../eng/module-consumers/README.md) builds v1/v2 on net8.0/net9.0/net10.0,
uses an isolated cache/feed with AgentMemory-only source mapping, and compares loaded DLL bytes to
the actual nupkgs. It preserves metadata, command and test receipts. Contract checks use a ProbeReader
test double and do not establish real storage, native host integration, migration or signed dynamic
lifecycle acceptance. No sample package is added to the release manifest or published by this work.

The independent `Commitments.NativeChecks` consumer additionally drives actual `ChatClientAgent`
invocations against an explicitly disposable loopback Neo4j service. Its deterministic terminal client
captures the inputs sent by the framework; it is not an inference provider or a storage double. The
runner requires a hash-pinned external world and disables recent/relevant core conversation recall.
Its primary-world results remain distinct from negative-fork, upgrade and signed lifecycle acceptance.
See [native consumer setup](../../eng/module-consumers/README.md#native-service-consumer).

## Module ingestion telemetry

Explicit `ModuleIngestion` emits one `memory.module.ingest` activity for each selected provider
attempt, on the existing `AgentMemoryDiagnostics.Source` (`AgentMemory`). The host chooses listeners
and exporters. The SDK adds neither a source per module nor a telemetry provider. Experimental span
and tag constants are in `AgentMemory.Extensibility.Diagnostics.ModuleTelemetry`.

Identity comes from the frozen resolution/provider binding: `memory.module.id`, `memory.module.version`
and `memory.provider.id`. `memory.module.fingerprint` is explicitly labelled with
`memory.module.fingerprint.kind=metadata`; it is not a signature or an activated runtime-set identity.
`memory.module.operation` distinguishes `ingest` and `replay`. Replay does not invoke providers again.
Store/database activities nest through Activity.Current under the selected provider's activity.

`memory.module.outcome` uses the existing provider outcome: Succeeded, Partial, Refused or Unavailable.
`memory.module.items` counts **known proposal outcomes** and `memory.module.refused` counts the refused
subset. Unavailable storage may have fewer known outcomes than attempted writes; these counters never
infer whether uncertain writes committed. Cancellation propagates with outcome Cancelled and no invented
item count. Unavailable/cancelled spans have Error status; policy refusals remain completed operations.

Tags contain no owner, source-message identity, excerpt, memory text, request fingerprint, refusal
content or raw exception text. Descriptor changes after binding cannot relabel the activity. Empty,
denied and unselected paths emit no ingestion span; invalid replay is rejected before any write/span.
No listener means no activity; when sampling requests no data, diagnostic counters are not computed.
Normal reports and exceptions remain authoritative even when telemetry is disabled.

Migration tracing is described below. Native trace acceptance and signed runtime load/unload remain
separate from listener unit checks.

## Module compiler telemetry

Use `services.AddAgentMemoryExtensibility(resolution)` or the `ContextCompiler` constructor whose
first argument is a successful `ModuleResolution` to bind contributors explicitly. Registration
replaces the compiler registration while preserving scoped lifetimes; register contributors separately.
The host supplies resolution metadata. Registration installs neither an ambient catalog nor an exporter.
The original constructor and parameterless registration keep their legacy behavior even if an unrelated
resolution is registered in DI; arbitrary contributors there emit no inferred module identities.

The bound compiler snapshots each descriptor once, requires unique contributor ids owned by resolved
modules, and preserves the existing `CoreMemoryContextContributor` separately. Failed resolution,
foreign ids, duplicate ids and an impersonated core id fail before contributor callbacks. This binding
is metadata validation, not a security sandbox, signed activation or runtime module-set lease.

`memory.module.compile` parents `memory.module.contribute` activities on the shared AgentMemory source.
Compilation carries the metadata fingerprint and `memory.module.count`: distinct bound modules with
at least one non-excluded contributor before applicability. Each contribution carries its frozen
module id/version and contributor id, its outcome and known item count. Database activities started
during a contribution inherit that contribution as parent. Typed omissions become
`memory.module.omission` events on compilation, containing only contributor id and omission reason.
The detailed omission remains in the envelope; private details are never copied into trace events.

Completed compilation is `Completed`, or `Partial` if a contributor was unavailable. Contribution
outcomes are `Section` or the existing typed omission reason. Caller cancellation propagates and marks
both activities `Cancelled`; unavailable/cancelled activities have Error status. Exclusions still occur
before applicability, without omission events. Empty, core-only and fully excluded compositions emit
no module activities. With no sampled data, instrumentation does not inspect section item counts.
Listener tests cover identity freezing, nesting, privacy, omissions, cancellation, DI compatibility,
scoped lifetimes and disabled paths. Native service trace acceptance remains separate.

## Module migration telemetry

Explicit `ModuleMigrator.PlanAsync` and `ApplyAsync` emit `memory.module.migrate` on the same source.
`memory.module.operation` distinguishes `plan`, `apply` and owned `step` execution. Plan/apply carry a
metadata-labelled resolution fingerprint without a module id: namespace reservation and SDK ledger
work belong to the composition. Each actual step is a child of apply and carries its resolved owner
id/version. Its database activities nest under that step. No actor, Cypher, problem text or raw
exception content is added to module tags/events.

Plan outcomes are `Pending`, `Completed` or `Refused`. Apply returns `NoOp` for an already applied set,
otherwise `Completed` only after the final assessment succeeds. A failure is `Unavailable`, a known
policy refusal `Refused`, and caller cancellation `Cancelled`; exceptions still propagate. Those three
failure outcomes have Error status. Assessment tags `memory.module.migration.pending`, `.applied` and
`.problems` count definitions/problems in the returned assessment, not newly executed statements or
modified records. A repeat no-op can therefore report previously applied definitions.

Empty schema composition emits no activity and makes no database call. Invalid actors and pre-cancelled
calls fail before tracing/database access. No-data sampling avoids telemetry-only assessment counts.
The instrumentation preserves the existing plan/apply, recovery and durable-intent engine. Controlled
transaction tests verify attribution, nesting, failure/cancellation, privacy and no-op behavior; a hosted
real-Neo4j test verifies successful steps and read-only repeat Apply. Native package trace acceptance
and hosted execution of this new integration test must be verified before claiming full acceptance.
