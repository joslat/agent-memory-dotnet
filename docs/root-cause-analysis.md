# Root Cause Analysis — Schema Divergence

> **Author:** Deckard (Lead / Solution Architect)
> **Date:** 2025-07-21
> **Severity:** Critical
> **Affected:** Every Neo4j query in the .NET implementation

---

## Executive Summary

The .NET implementation of Agent Memory diverged from the Python reference schema in **45+ places**, making it impossible to share a Neo4j database between Python and .NET clients. This was not an accident — it was the predictable result of a spec that explicitly chose "conceptual alignment" over schema parity, combined with insufficient validation against the Python codebase.

---

## 1. Why Did the Schema Diverge?

### 1.1 The Spec Said "Concepts, Not Code"

The root cause starts in the specification. Section 0.1 states:

> *"We reuse its concepts and architecture — not its code."*

Section 1.4 reinforces:

> *"not a fork or rebranding of the upstream Python project"*

The implementation plan §5.1 doubles down:

> *"We do not reuse code directly. We reuse: the memory concept, the three-memory model, the integration pattern, the graph-native approach."*
>
> *"We do not copy: Python-specific package structure, Python NLP stack, Python framework adapters."*

**This created a culture of independence from the Python schema.** Developers read these directives as permission to use .NET conventions for Neo4j property names, relationship names, and data types. Nobody asked: "But what about the actual database schema?"

### 1.2 The Spec Never Defined Neo4j Property Names

The specification (§3.1) defines domain model fields using C# PascalCase:
- `MessageId`, `SessionId`, `TimestampUtc`, `CreatedAtUtc`

It never specifies what these become in Neo4j. The index tables in §3.5 hint at `camelCase` (`sessionId`, `timestamp`, `taskEmbedding`), but these were written by the architect (me) without cross-referencing the actual Python Cypher queries. **The spec's index tables themselves are wrong** — they show `camelCase` where Python uses `snake_case`.

### 1.3 The Implementation Plan Inherited the Spec's Ambiguity

Plan §9.1–§9.3 lists node labels, relationships, and constraints but never specifies:
- Property naming convention for Neo4j
- Exact property names to use
- Whether to match Python's Cypher property names

The plan correctly lists most relationship types but uses names from spec discussions rather than from the Python source code. Nobody ran `grep -r "RELATED_TO\|RELATES_TO" queries.py` to verify.

### 1.4 .NET Developers Applied .NET Conventions

When developers wrote repository code, they naturally used `camelCase` for Neo4j properties because:
1. The spec's own index tables showed `camelCase`
2. .NET convention is `camelCase` for JSON/data properties
3. Nobody said "use `snake_case`"
4. Nobody provided a property-name mapping table

Similarly, relationship names drifted:
- `RELATES_TO` instead of `RELATED_TO` — a simple brain-swap that felt right
- `USED_TOOL` instead of `USES_TOOL` — past tense felt more natural
- `CALLS` instead of `INSTANCE_OF` — seemed more descriptive

**Nobody checked the Python Cypher queries before writing .NET Cypher queries.**

### 1.5 Was the Divergence Intentional?

**Partially.** Roy's history explicitly states (2025-07-20):

> *"Schema Incompatibilities (same Neo4j instance won't work for both): property naming — snake_case (Python) vs camelCase (.NET) throughout; relationship type names differ: RELATED_TO vs RELATES_TO, USES_TOOL vs USED_TOOL, INSTANCE_OF vs CALLS"*

This was documented as a **known fact**, not as a **bug to fix**. The team treated it as an acceptable consequence of the "concepts, not code" approach. Nobody escalated it as a problem because nobody was told schema parity was a requirement.

---

## 2. Why Do 45+ Gaps Exist?

### 2.1 Architecture Issues

**The spec has no schema contract.** There is no authoritative schema document that both implementations must conform to. The Python code IS the schema, but the .NET spec never says "match Python's Cypher exactly."

**No shared schema artifact.** Both implementations define schema independently:
- Python: `graph/schema.py` + `graph/queries.py`
- .NET: `SchemaBootstrapper.cs` + individual repository files

There's no shared `.cypher` file, no shared JSON schema, no Cypher DDL file that both must implement.

### 2.2 Design Issues

**Missing translation layer.** The .NET architecture correctly separates domain models (PascalCase) from Neo4j persistence, but the repository layer was designed to use the same casing as the domain models. There's no explicit "Neo4j property name mapper" that converts `SessionId` → `session_id`.

**Premature property naming in the spec.** The spec's §3.5 index definitions used `camelCase` property names (e.g., `sessionId`, `taskEmbedding`), which misled developers into thinking this was the correct Neo4j convention.

### 2.3 Planning Issues

**No schema validation step in the bootstrap order.** Decision D4 defines the build sequence but has no step that says "validate against Python schema." There's no gate that checks schema parity.

**Incomplete Python analysis.** The initial Python analysis focused on architecture and concepts. The detailed schema extraction (`docs/python-agent-memory-analysis.md`) was done after implementation had already started. By then, conventions were set.

### 2.4 Execution Issues

**No schema contract tests.** There is no test that:
1. Reads the Python `schema.py` and `queries.py`
2. Reads the .NET `SchemaBootstrapper.cs` and repository queries
3. Asserts they produce the same Neo4j schema

**No cross-implementation validation.** Nobody ran both Python and .NET against the same Neo4j instance and compared the actual graph.

**Incremental drift.** Each repository was implemented independently. Each developer made the same `camelCase` decision independently because the pattern was set by the first repository and copied forward.

### 2.5 Was the Spec Incomplete?

**Yes, critically.** The spec is missing:

1. **A canonical property-name mapping table.** It should map every C# domain property to an exact Neo4j property name.
2. **A statement on schema parity.** It should say: "The .NET implementation MUST produce the same Neo4j schema as the Python reference implementation."
3. **Relationship names table.** It lists some relationships but doesn't provide the complete list from Python.
4. **Data type requirements.** It doesn't specify whether timestamps should be Neo4j `datetime()` or ISO 8601 strings.
5. **A schema validation requirement.** It should require schema contract tests.

---

## 3. What Went Wrong in Our Process?

### 3.1 We Didn't Validate Against Python Enough

The Python codebase was analyzed for **architecture** but not for **schema details**. We read the Python code to understand memory layers, not to extract exact Cypher property names.

**Specific failures:**
- Nobody ran `grep -rn "session_id\|created_at\|updated_at" queries.py` before writing .NET queries
- Nobody compared Python's `CREATE_ENTITY` query against our `Neo4jEntityRepository.UpsertAsync()` Cypher
- Nobody listed all Python relationship types and cross-checked against .NET

### 3.2 Review Checkpoints Were Insufficient

The formal architecture review (2025-01-28) checked:
- ✅ Package boundaries
- ✅ Dependency direction
- ✅ No MAF/GraphRAG leakage
- ✅ No speculative features

It did NOT check:
- ❌ Neo4j property names match Python
- ❌ Relationship types match Python
- ❌ Index definitions match Python
- ❌ Data types match Python

**The review was focused on .NET architecture, not on Neo4j schema parity.**

### 3.3 We Should Have Had Schema Contract Tests

The single biggest process failure. If we had a test that:
1. Parsed Python `queries.py` for all property names and relationship types
2. Parsed .NET repository files for all Cypher strings
3. Compared them

...every divergence would have been caught on the first PR.

### 3.4 The "Not a Fork" Mindset Went Too Far

The spec correctly says we shouldn't copy Python's code structure, NLP stack, or framework adapters. But it conflates code independence with schema independence. The Neo4j schema is a **data contract**, not an implementation detail. You can have completely different code that produces the same schema.

---

## 4. Honest Assessment: Who/What is Responsible?

| Factor | Responsibility | Weight |
|--------|---------------|--------|
| Spec ambiguity on property naming | Deckard (architect) | 30% |
| "Concepts not code" over-interpretation | Team culture | 20% |
| No schema contract tests | Deckard (process) | 20% |
| Spec §3.5 showing camelCase index props | Deckard (spec error) | 15% |
| No review checkpoint for schema parity | Deckard (review design) | 10% |
| Independent repository implementation | Developers (execution) | 5% |

**The architect (Deckard) bears primary responsibility.** The spec was written without a clear schema contract, the review process didn't catch schema drift, and no automated validation was required.

---

## 5. Recommendations

### 5.1 Immediate Actions

1. **Create the schema document** (done — see `docs/schema.md`).
2. **Fix P0 items** — property naming, relationship names, datetime handling, missing indexes/constraints.
3. **Write a migration** for existing databases.
4. **Update the spec** to explicitly require Python schema parity.

### 5.2 Schema Contract Testing

Create an integration test class `SchemaParityTests` that:

```csharp
[Fact]
public async Task AllPropertyNames_MustUseSnakeCase()
{
    // Extract all property names from repository Cypher strings
    // Assert none contain camelCase patterns
}

[Fact]
public async Task RelationshipTypes_MustMatchPythonCanonical()
{
    // List: RELATED_TO, USES_TOOL, INSTANCE_OF, HAS_MESSAGE, etc.
    // Assert all .NET Cypher strings use these exact names
}

[Fact]
public async Task Indexes_MustMatchPythonCanonical()
{
    // Parse SchemaBootstrapper index definitions
    // Assert all Python indexes are present
}
```

Additionally, add a **Cypher linter** to CI that greps all `.cs` files for Cypher strings and validates:
- Property names are `snake_case`
- Relationship types are in the approved list
- No `camelCase` properties in Cypher strings

### 5.3 Ongoing Parity Validation

1. **Schema parity checklist** in every PR template:
   - [ ] Neo4j property names use `snake_case`
   - [ ] Relationship types match `docs/schema.md`
   - [ ] New indexes added to SchemaBootstrapper
   - [ ] Timestamps use `datetime()` not strings

2. **Shared schema artifact:** Maintain `docs/schema.md` as the single source of truth. Every schema change must update this document first, then both implementations.

3. **Cross-implementation smoke test:** Monthly, spin up a Neo4j instance, run Python's `setup_all()`, then run .NET's `BootstrapAsync()`, and verify no conflicts.

### 5.4 Process Changes

1. **Add a schema review gate** to the development process. Before any repository implementation, the Cypher queries must be reviewed against `docs/schema.md`.

2. **Property name mapping module.** Create a `CypherPropertyNames` static class that centralizes all Neo4j property name constants:
   ```csharp
   public static class CypherPropertyNames
   {
       public const string SessionId = "session_id";
       public const string CreatedAt = "created_at";
       public const string UpdatedAt = "updated_at";
       // ... all properties
   }
   ```
   This prevents individual developers from guessing property names.

3. **Update the spec.** Add to §3.1: *"All Neo4j node and relationship properties MUST use `snake_case` naming to maintain schema parity with the Python reference implementation."*

---

## 6. Lessons Learned

1. **A data schema is not an implementation detail.** Even when code is independent, the database schema is a shared contract.
2. **"Concepts, not code" needs a boundary.** The architecture can differ. The database schema cannot.
3. **Specs must be explicit about naming conventions.** Ambiguity in a spec becomes divergence in code.
4. **Automated validation beats review checklists.** A human will miss `camelCase` vs `snake_case`. A test will not.
5. **The first repository sets the pattern.** If the first repository uses `camelCase`, all subsequent ones will copy it.

---

## Appendix: Timeline of Divergence

| Date | Event | Impact |
|------|-------|--------|
| 2025-01-28 | Spec written with "concepts not code" directive | Set the mindset |
| 2025-01-28 | Architecture review: no schema parity check | Missed the gate |
| 2025-01-28 | Spec §3.5 written with camelCase index property names | Misled developers |
| 2025-01-28+ | First repository implemented with camelCase | Set the pattern |
| 2025-01-28+ | All subsequent repositories copied camelCase | Drift amplified |
| 2025-07-12 | Python analysis revealed missing indexes | Partial fix (added to spec) |
| 2025-07-13 | Spec updated with 18 indexes, cross-memory relationships | Indexes added but still camelCase |
| 2025-07-20 | Roy documented schema incompatibilities | Acknowledged but not treated as bug |
| 2025-07-21 | Jose discovers divergence: "WE MUST USE THE SAME SCHEMA!!!" | This document |
