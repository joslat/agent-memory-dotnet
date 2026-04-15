# Architecture Review Decisions — Deckard

**Date:** July 2026  
**Trigger:** Comprehensive architecture audit of all 10 source packages  
**Status:** Proposed (pending Jose approval)

---

## D-AR1: Merge Extraction Packages with Strategy Pattern

**Proposal:** Merge `Extraction.Llm` and `Extraction.AzureLanguage` into a unified `Extraction` base package with an `IExtractionEngine` strategy interface. Keep engine-specific NuGet dependencies in sub-packages (`Extraction.Llm`, `Extraction.AzureLanguage`).

**Rationale:** ~95% structural duplication. Same 4 interfaces, same error handling, same pipeline. Only the engine (IChatClient vs TextAnalyticsClient) differs. Strategy pattern eliminates duplication and enables runtime engine selection.

**Impact:** HIGH — Reduces code duplication, simplifies new engine onboarding, enables runtime switching.  
**Risk:** Breaking change for current consumers. Mitigate with semantic versioning.

---

## D-AR2: Consolidate Embedding Generation into IEmbeddingOrchestrator

**Proposal:** Extract embedding text-composition and call logic from 5+ call sites into a single `IEmbeddingOrchestrator` service in Core.

**Rationale:** `GenerateEmbeddingAsync()` is called from ShortTermMemoryService (2×), LongTermMemoryService (3×), MemoryExtractionPipeline (3×), MemoryContextAssembler (1×), and MemoryService batch methods. Each site has its own text composition and error handling.

**Impact:** HIGH — Eliminates most DRY violations, single point for embedding strategy changes.  
**Risk:** LOW — Internal refactor only.

---

## D-AR3: Keep Observability as Separate Package

**Proposal:** Retain `Neo4j.AgentMemory.Observability` as a separate package despite its small size (427 LOC).

**Rationale:** Observability is opt-in. Moving to Core would force OpenTelemetry.Api dependency on all consumers. Separate package signals first-class support while keeping Core lean.

**Impact:** None (no change).  
**Risk:** None.

---

## D-AR4: Resolve Dual Pipeline Ambiguity

**Proposal:** Clarify the relationship between `MemoryExtractionPipeline` and `MultiExtractorPipeline`. Rename to `DefaultExtractionPipeline` and `MultiProviderExtractionPipeline`, and document selection criteria in DI registration comments.

**Rationale:** Two `IMemoryExtractionPipeline` implementations exist with no clear guidance on when to use which. This creates confusion for consumers.

**Impact:** MEDIUM — Reduces consumer confusion.  
**Risk:** LOW — Naming change only.

---

## D-AR5: Publish Meta-Package for Quick Start

**Proposal:** Create a `Neo4j.AgentMemory` convenience meta-package that references Abstractions + Core + Neo4j + Extraction.Llm.

**Rationale:** Current onboarding requires installing 3+ packages. Meta-package reduces friction to a single `dotnet add package Neo4j.AgentMemory`.

**Impact:** HIGH — Significantly improves developer experience.  
**Risk:** None — Empty project with dependency declarations.
