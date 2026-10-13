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
ontology and view checks below. Whole-set SDK/dependency/host resolution and trust/activation remain unfinished. Do not treat a successful parse as
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
Whole-set validation, signature checks and runtime activation are still separate unfinished work.
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
The editor JSON Schema and generated index/constraint-name collision checks are still unfinished;
this API alone does not close the module-resolution capability.
