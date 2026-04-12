### 2026-04-12T22:30: Phase 2 Architecture Decisions
**By:** Deckard (Lead Architect)
**What:**
- D7: Extraction.Llm project created with Microsoft.Extensions.AI.Abstractions for IChatClient-based LLM extraction
- D8: FuzzySharp 2.0.2 added to Core for entity resolution fuzzy matching (C# port of fuzzywuzzy/RapidFuzz)
- D9: Entity resolution chain implemented in Core (not in Neo4j layer) — resolution is business logic, not persistence
- D10: Entity resolution metadata (match_type, confidence, merged_from) captured via new EntityResolutionResult record in Abstractions
**Why:** Phase 2 requires LLM-backed extraction and real entity resolution per Python agent-memory analysis. FuzzySharp provides .NET-native fuzzy string matching equivalent to Python's RapidFuzz.
