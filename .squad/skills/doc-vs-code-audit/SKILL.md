# Skill: doc-vs-code-audit

**Domain:** Documentation quality  
**Author:** Roy  
**Last Used:** 2025-07-23  

## Purpose

A systematic procedure for auditing documentation accuracy against what is actually in src/, tests/, and samples/. Finds stale numbers, removed components, renamed APIs, and dead references.

## When to Use

- Before any external release or public README update
- After a refactoring wave that renames, removes, or merges components
- When team members report "the docs don't match the code"
- As a scheduled ceremony (quarterly)

## Procedure

### Step 1 — Enumerate actual surface area
```
src/    → count *.csproj files; list package names
tests/  → count test projects; grep [Fact]+[Theory] for test count
src/McpServer/ → grep for tool/resource/prompt method counts
src/Abstractions/Services/ → list *.cs for interface count
src/Abstractions/ → list all *.cs for domain type count
```

### Step 2 — Collect doc claims
For each major doc (README, architecture.md, design.md, implementation-status.md):
- Find package lists and counts
- Find test counts
- Find interface/type counts
- Find feature counts (tools, resources, etc.)
- Find API names used in examples

### Step 3 — Cross-reference
| Claim | Doc Source | Actual | Match? |
|-------|-----------|--------|--------|
| Package X exists | doc | src/ listing | ✅/❌ |
| N unit tests | doc | grep count | ✅/❌ |

### Step 4 — Classify findings
- **Tier 1 (Definitely Stale):** concrete evidence that claim is wrong
- **Tier 2 (Likely Stale):** directionally probably wrong; verify
- **Tier 3 (Archive/Delete candidates):** superseded or misplaced

### Step 5 — Check for dead references
Grep each doc for paths/filenames; verify they exist on disk.

## Common Drift Patterns in This Codebase

| Drift Type | Where to Look | What Drifts |
|------------|--------------|-------------|
| Package merges | README table, architecture.md §3.4.x | Package listed as separate when merged |
| Interface renames | design.md §5 service catalog | Old interface name after MEAI migration |
| Test count | README, implementation-status, architecture | Never auto-updated after new tests |
| MCP tool count | README, feature-record, architecture | Counted per-method, not per-class |
| .NET version | README prerequisites | Project changed from net8 to net9 |
| Quick Start APIs | README §Getting Started | Type/method names don't match actual public API |
| MEAI boundary rules | architecture.md §5 | "zero MEAI matches" claim stale after migration |

## Key Grep Commands

```powershell
# Count all test methods
Get-ChildItem tests -Recurse -Filter "*.cs" | Select-String '\[Fact\]|\[Theory\]' | Measure-Object

# Count MCP tool methods
Get-ChildItem src\Neo4j.AgentMemory.McpServer\Tools -Recurse -Filter "*.cs" | 
  ForEach-Object { (Select-String -Path $_.FullName -Pattern 'public.*Async').Count }

# List all src packages
Get-ChildItem src -Recurse -Filter "*.csproj" | Select-Object Name

# List all service interfaces
Get-ChildItem src\Neo4j.AgentMemory.Abstractions\Services -Filter "*.cs" | Select-Object Name

# Verify API exists
Get-ChildItem src -Recurse -Filter "*.cs" | Select-String 'IAgentMemory|StoreMessageAsync|AssembleContextAsync'
```

## Output Format

Write findings to `.squad/decisions/inbox/{agent}-doc-audit.md` using Tier 1/2/3 structure.  
Append to agent history.md under `## Learnings`.
