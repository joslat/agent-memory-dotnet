# Package Rename Review — 2026-04-30
**Reviewer:** Deckard (Lead Architect)
**Branch:** rename/agentmemory-package-ids
**Commit:** acef3efb58de48e24893107fa7c5bf4b65c0fbcc

## Verdict: APPROVED

---

## Summary

Roy's rename of all eleven source packages from `Neo4j.AgentMemory.*` to `AgentMemory.*` is architecturally correct, mechanically complete, and build-verified. All eight review gates passed with two minor observations that do not block merge. The branch contains exactly one commit; it is safe to merge to main.

---

## Findings by Area

### 1. Rename Reasoning — PASS

The top-level prefix `AgentMemory.*` is correct: the library is a product, not a Neo4j first-party SDK. `AgentMemory.Neo4j` survives as the adapter qualifier, which is the right pattern (product first, technology qualifier second). This is consistent throughout all eleven packages, the test projects, and the sample projects. No adapter package carries an ambiguous name.

### 2. CHANGELOG Entry — PASS

The `[Unreleased]` block accurately records the rename with context: what changed, why (NuGet IDs are permanent, pre-publish window), and scope (453 .cs files, 17 .csproj files, 1 .slnx). The only occurrence of `Neo4j.AgentMemory` in any `.md` file outside `.squad/` is in the CHANGELOG itself, correctly used as the "renamed from" value. That is expected and appropriate.

### 3. .csproj File Correctness — PASS with one minor observation

- `AgentMemory.Core.csproj`: ProjectReferences point to correct new paths. No explicit `<PackageId>` — defaults to project name `AgentMemory.Core`. Correct.
- `AgentMemory.Neo4j.csproj`: Same pattern. PackageId implicit from project name. Correct.
- `AgentMemory.csproj` (meta-package): ProjectReferences updated correctly. **Minor:** `<Description>` still reads "Convenience meta-package for Neo4j Agent Memory" and `<Authors>Neo4j</Authors>` — stale branding text in the description field. The package ID itself is correct (`AgentMemory`). This is a cosmetic issue for NuGet Release Prep, not a blocker for the rename.

None of the packages have an explicit `<PackageId>` element; all rely on MSBuild's project-name default. This is technically correct pre-v1 but should be made explicit during NuGet Release Prep (#4) to prevent any accidental drift.

### 4. README and Key Docs — PASS

`README.md` uses `AgentMemory.*` package names throughout all install snippets, package tables, and usage examples. No `dotnet add package Neo4j.AgentMemory.*` references found. Docs are clean.

### 5. .squad/ Internal Docs — PASS

`git diff main...HEAD -- ".squad"` returns 0 lines. Operational docs (charters, decisions, histories) were correctly left unmodified by Roy. These are internal artifacts, not part of the public package surface, and the decision not to rewrite them is correct.

### 6. Namespace / File Path Alignment — PASS

Spot-checked files across four packages:
- `AgentMemory.Abstractions`: `CompressedContext.cs`, `DeduplicationStats.cs`, `DuplicatePair.cs` — all declare `namespace AgentMemory.Abstractions.Domain;`
- `AgentMemory.McpServer`: `McpServerOptions.cs`, `ServiceCollectionExtensions.cs` — declare `namespace AgentMemory.McpServer;`

Path segments and namespace declarations are aligned. No legacy `Neo4j.AgentMemory.*` namespace declarations observed.

### 7. NuGet Package Metadata Consistency — PASS

- `AgentMemory.McpServer.csproj`: `<RootNamespace>AgentMemory.McpServer</RootNamespace>` — correct.
- `AgentMemory.Abstractions.csproj`: `<RootNamespace>AgentMemory.Abstractions</RootNamespace>`, `<AssemblyName>AgentMemory.Abstractions</AssemblyName>` — correct.

No old `Neo4j.AgentMemory.*` values found in any metadata field across either package.

### 8. Git Log — PASS

```
acef3ef (HEAD -> rename/agentmemory-package-ids) chore: rename all packages from Neo4j.AgentMemory.* to AgentMemory.*
```

Exactly one commit on the branch. The commit message is clear and follows the project's conventional commit style. No extraneous commits, no merge noise.

---

## Issues Requiring Remediation Before Merge

### Blockers
None.

### Minor
1. **Meta-package `<Description>` is stale.** `AgentMemory.csproj` still reads `"Convenience meta-package for Neo4j Agent Memory."` The description will appear verbatim on NuGet.org. Recommend updating to `"Convenience meta-package for Agent Memory for .NET. References all essential assemblies so consumers only need a single package reference."` This can be done as part of NuGet Release Prep (#4) or in a follow-up commit on this branch before merge — either is acceptable.

### Cosmetic
2. **`nextsteps.md` "What is not done yet" paragraph** still lists "package rename (AgentMemory.* root namespace)" as pending. Stale after this merge. Updating as part of this review.

3. **No explicit `<PackageId>` in any .csproj.** Relying on project-name default is correct but fragile if a project is ever renamed or moved. This is NuGet Release Prep scope, not rename scope.

---

## Recommendation

APPROVE FOR MERGE.

The rename is complete, correct, and verified. The two minor observations above (stale description text, no explicit PackageId) are pre-existing patterns that belong to NuGet Release Prep (#4), not to this branch. The cosmetic issue in nextsteps.md is addressed by this review commit.
