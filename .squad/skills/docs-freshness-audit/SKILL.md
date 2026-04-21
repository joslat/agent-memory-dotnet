# SKILL: Documentation Freshness Audit

**Category:** Docs / DX  
**Author:** Joi  
**First extracted:** 2026-07-25

---

## What This Skill Does

A structured method for auditing an entire docs corpus: identifying stale numeric claims, frozen planning docs, superseded analyses, missing docs, and misplaced files. Produces a prioritized, rationale-backed recommendation list.

---

## When To Use

- Before a public release or first NuGet publish
- After any major implementation sprint
- When docs/ has grown organically and needs a health check
- When a team member says "I can't trust our docs"

---

## Procedure

### Step 1 — Inventory All Docs

Use glob `**/*.md` to find every markdown file. Group by:
- Root-level files (README, SPEC, PLAN, etc.)
- `docs/` directory
- `samples/*/README.md`
- Internal files (`.squad/`, `.copilot/`)

### Step 2 — Extract All Numeric Claims

Grep for numeric "authority signals" across docs:
```powershell
# Test counts
Select-String -Pattern "\d+ (unit |passing |total )tests?" docs/*.md
# Tool counts
Select-String -Pattern "\d+ tools?" docs/*.md
# Package counts
Select-String -Pattern "\d+ packages?" docs/*.md
```

### Step 3 — Ground-Truth From Code

Verify claimed numbers against actual code state:
```powershell
# MCP tools — use the EXACT attribute name, not broad substring
Get-ChildItem src/ -Recurse -Filter "*.cs" | 
  Select-String -Pattern "\[McpServerTool\]|\[McpServerTool\(" | Measure-Object
  
# Test methods — count [Fact] and [Theory] attributes
Get-ChildItem tests/ -Recurse -Filter "*.cs" | 
  Select-String -Pattern "^\s+\[Fact\]|^\s+\[Theory\]" | Measure-Object

# Source projects
Get-ChildItem src/ -Filter "*.csproj" -Recurse | Measure-Object
```

⚠️ **Critical:** Use precise, targeted grep patterns. `Tool(` will match `ToolCall(`, `CreateTool(`, etc. and give false positives.

### Step 4 — Classify Each Doc

For each doc, ask:
1. **Purpose:** What problem did this doc solve when written?
2. **Audience:** Who reads it today?
3. **Drift:** Do its numeric claims match code? Do status labels match current reality?
4. **Supersession:** Is there a newer doc that covers the same material more accurately?

Use this classification:

| Class | Meaning | Action |
|-------|---------|--------|
| 🟢 Current | Accurate, actively referenced | Keep |
| 🟡 Needs update | Core content valid; specific claims stale | Fix targeted sections |
| 🟠 Frozen/Historical | Work is done; doc was the plan | Archive to `docs/archive/` |
| 🔴 Superseded | Newer doc covers same ground better | Delete or archive |
| 🔵 Misplaced | Content is fine; location is wrong | Move |
| ⚫ Missing | Known gap; doc doesn't exist | Create |

### Step 5 — Special Watch: "Authoritative" Title Docs

Docs with titles implying current authority ("Status", "Assessment", "Review", "Reference") are the **highest risk** for frozen staleness. They were written to be authoritative at a point in time, and readers treat them as current even when they're not.

Check these extra carefully:
- `implementation-status.md` — Does Phase X still say "in progress"?
- `architecture-review-assessment.md` — Are "Future" items actually shipped?
- Any doc with "✅ COMPLETE" or "DONE" in it — these are safe. Items still showing plans are suspect.

### Step 6 — Check for Missing Critical Docs

A project without these is incomplete for public use:
- `docs/getting-started.md` or `docs/quickstart.md`
- `CONTRIBUTING.md`
- `CHANGELOG.md`
- Public API documentation

### Step 7 — Produce Prioritized Recommendations

Group by:
1. **Critical corrections** — Wrong facts that will mislead users
2. **Archive candidates** — Docs no longer needed as guidance
3. **Purpose clarification** — Docs that need a "status label" or "what this is" header
4. **Missing docs** — Gaps to fill

---

## Patterns Observed

### "Boundary Rule Assertion Drift" Anti-Pattern (Architect Concern)

Architecture docs often contain "Current Verification" sections that assert compliance at a point in time (e.g., "grep for Package X returns zero matches"). As the codebase evolves, these assertions become false without anyone noticing — because they're prose, not tests.

**Example found:** `docs/architecture.md §5` said "Abstractions .csproj: zero PackageReference entries" and "grep for Microsoft.Extensions.AI returns zero matches." Both became false after D-AR2-1 (MEAI adoption) — but the verification section was never updated.

**Mitigation:**
- Never write "Current Verification" sections without tagging them with the date and PR/decision they were verified against.
- Periodically re-run the grep commands listed in boundary rule verification sections and confirm they still hold.
- If a boundary rule changes (e.g., an exception is added), update the rule text AND the verification section.

---

### "Deleted Package Ghost" Anti-Pattern

A package is merged or deleted, but its full architectural description remains in docs as a live entry. Readers treat the section as authoritative.

**Example found:** `docs/architecture.md §3.4.2` described `Neo4j.AgentMemory.GraphRagAdapter` with purpose, dependencies, key types, and namespace structure — but that package was merged into the Neo4j package and deleted from `src/`.

**Mitigation:**
- When a package merge/delete decision is made, immediately create a docs issue or task to remove the corresponding architecture section.
- When auditing, verify every package-section in architecture docs against the actual `src/` directory.

---

### "Sprint Update False Positive" Anti-Pattern

A doc is updated in a sprint to "fix" a number, but the grep used to verify was too broad. The doc now contains a wrong number and the history records it as "verified from code." This is particularly insidious because the history appears to show due diligence.

**Mitigation:** Always state the exact grep pattern used when recording a code-verified count in history.

### "Planning Doc Freeze" Pattern

Docs written during Phase 0–1 (spec, plan, analysis) are excellent context for the team. After Phase 6 completes, they become frozen artifacts. No one updates them because there's no sprint tasking it.

**Mitigation:** After final sprint, schedule a "doc close-out" task that adds an `[ARCHIVED — Implementation Complete]` banner to every planning doc.

### "Cascade Staleness" Pattern

One doc's stale number propagates into other docs when those docs reference or copy it. Fix the root doc, then propagate the fix.

---

## Related Skills

- `.squad/templates/skills/docs-standards/SKILL.md` — standards for writing new docs
- `.squad/skills/maf-adapter-audit/SKILL.md` — code audit pattern (analogous approach)
