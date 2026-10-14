# Experimental module SDK

The current core release inventory holds `AgentMemory.Extensibility` and its framework adapter
on their separate experimental version line. The source contains context contributors/compiler,
supported framework admission methods, and the resolution primitives below. It does not yet contain
the complete manifest/profile resolver, module store, migrations or runtime loader. Registering
contributors is not module-platform activation.

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
not an arbitrary Cypher security sandbox. Embedded source discovery is available below; handwritten
namespace checks, execution and CLI integration remain unfinished. No migration runs during registration. The portable SDK remains
independent of Neo4j.

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
hosts. All pending hashes and claims commit before any module DDL. DDL and applied stamps are
separate transactions: a crash retains `applying` intent and a matching retry resumes idempotently.
Foreign ownership, mutated hashes, changed/missing applied shapes and failed indexes are refused.
Previously applied keys omitted by an upgrade are reported and retained. A relationship-only upgrade
still reserves its namespace even when no new index is required.

This API currently applies **generated schema only**. It does not load hand-written scripts, expose
CLI commands, activate a module, or implement runtime first-write readiness. Those remain separate
unfinished integration work. Administrators must serialize external schema changes with application;
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

Discovery and compilation do not authorize execution. These definitions cannot yet be supplied to
`ModuleMigrator`: handwritten database assessment, multi-statement recovery and CLI wiring remain
unfinished. Backfills may be non-idempotent (for example, increments); execution must commit their
statement progress in the same transaction as the update before enabling retries. Existing generated
schema application and built-in migrations are unchanged. No handwritten database acceptance is claimed.
