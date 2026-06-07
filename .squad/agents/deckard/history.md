# Deckard — History (Summarized)

## Project Context
- **Project:** Agent Memory for .NET — native .NET Neo4j Memory Provider for AI agents
- **Stack:** .NET 9, C#, Neo4j, Microsoft Agent Framework, GraphRAG
- **Architecture:** Layered ports-and-adapters (Abstractions → Core → Neo4j → Adapters)

## Recent Work Summary (Waves 1-4)
- **Wave 1:** IEmbeddingOrchestrator + ExtractorBase<T> ✅
- **Wave 2:** Pipeline SRP Split + Confidence Thresholds ✅
- **Wave 3:** Cypher Query Centralization (140 queries) ✅
- **Wave 4:** Functional Parity Domain Types + Post-Refactoring Assessment ✅

## Current Status
- **Build:** 0 errors, 1,211 tests passing
- **Architecture:** Clean dependency graph, zero circular deps
- **Queries:** 140 centralized constants, 21 residual inline (down from 207+)

## Latest Decision (2026-05-08)
- Banned models: claude-opus-4.7, gpt-5.5
- Preferred set: claude-sonnet-4.6, gpt-5.3-codex, claude-haiku-4.5
- Priority source: .squad/identity/now.md (supersedes docs/nextsteps.md)

## Next Steps
1. NuGet Release Prep (immediate)
2. Streaming Extraction (Phase 2)
3. Local Embedding Adapter (Phase 3)

**See history-archive.md for full detailed history.**
