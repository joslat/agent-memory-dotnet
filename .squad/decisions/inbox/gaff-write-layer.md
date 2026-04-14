# Decision: Fact MERGE Key Changed from ID to SPO Triple

**Author:** Gaff (Neo4j Persistence Engineer)
**Date:** 2025-07-15
**Status:** Implemented

## Context
Fact UpsertAsync previously used `MERGE (f:Fact {id: $id})` which allowed duplicate subject-predicate-object triples when different IDs were generated for semantically identical facts.

## Decision
Changed the MERGE key to `MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object})`. The `id` is now set inside `ON CREATE SET` so existing facts matching the same SPO triple get updated (via `ON MATCH SET`) instead of duplicated.

Added `FindByTripleAsync` for pre-flight dedup checks and `updated_at` timestamp on match.

## Impact
- **Core services** that call `IFactRepository.UpsertAsync` get dedup for free — no code changes needed
- **Entity merge** now clears `target.embedding = null` to flag re-embedding after aliases change
- **Conversation** now has a `Title` property persisted as `c.title` in Neo4j (snake_case)
- **FakeResultCursor** test helper added — use this instead of `Substitute.For<IResultCursor>()` when mocking `SingleAsync`/`ToListAsync`
