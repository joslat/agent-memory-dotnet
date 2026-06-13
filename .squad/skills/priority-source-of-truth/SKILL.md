---
name: "priority-source-of-truth"
description: "Resolve conflicting squad priority documents by recency and operational role"
domain: "planning"
confidence: "high"
source: "observed"
---

## Context
Use this when squad artifacts disagree about what is current work versus historical rationale. This codebase keeps both long-lived planning docs and short operational status docs, so priority drift can happen after major milestones.

## Patterns
- Prefer the newest operational artifact first: `.squad/identity/now.md`
- Use `docs/nextsteps.md` for sequencing rationale and backlog detail, not as the authority when it conflicts with `now.md`
- If a task appears started in one file and absent in the newer status file, flag it as a governance ambiguity and force an explicit keep/de-scope decision
- Before routing work, reconcile the current-focus list and the forward-looking roadmap so downstream agents do not optimize for stale gates

## Examples
- `now.md` says release prep is the current focus, while `docs/nextsteps.md` still treats Aspire Demo as a release gate → use `now.md` for execution, record a decision, and clean up the drift

## Anti-Patterns
- Treating every planning document as equally current
- Routing work from an older roadmap without checking the newer operational status file
- Leaving a “started” item dangling across releases without a formal keep/de-scope decision
