# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, design decisions | Deckard | Package boundaries, dependency direction, ADRs |
| Code review | Deckard | Review PRs, approve/reject, enforce architecture |
| Domain models, abstractions, core | Roy | Interfaces, models, orchestration, context assembly |
| Extraction abstractions | Roy | IEntityExtractor, IFactExtractor, pipeline contracts |
| Neo4j persistence | Gaff | Repositories, Cypher, schema, indexes, migrations |
| Neo4j search (vector/fulltext) | Gaff | Search wrappers, query optimization |
| MAF adapter | Rachael | Context provider, message store, tools, lifecycle |
| Agent Framework integration | Rachael | Pre/post-run hooks, trace recorder, MAF samples |
| GraphRAG adapter | Sebastian | GraphRAG wrapper, blend policy, retrieval modes |
| GraphRAG + memory blend | Sebastian | Blended context assembly, feature toggles |
| Testing, test harness | Holden | Unit/integration/E2E tests, Testcontainers, fixtures |
| Test infrastructure | Holden | Docker Compose for tests, golden datasets, seeders |
| Documentation | Joi | README, architecture docs, samples, ADR writing |
| Developer experience | Joi | Getting-started guides, API docs, examples |
| Scope & priorities | Deckard | What to build next, trade-offs, decisions |
| Session logging | Scribe | Automatic — never needs routing |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
