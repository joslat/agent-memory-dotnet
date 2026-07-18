# NAMS Phase 10e: TCK Platinum Live-Probe Spike — Findings & Plan

## Purpose

Before implementing any of the TCK Platinum capability areas (conversation-lifecycle completion,
entity feedback + graph, reasoning/provenance, Cypher query console), verify the exact live shape of
every endpoint the pinned OpenAPI snapshot (`docs/reviews/nams-openapi-snapshot-2026-07-17.json`)
documents for them. This repo's own history (Phase 10a's empty-content endpoint inconsistency, Phase
10b's `"deleted"` vs the plan's assumed `"success"` delete-response status) shows "documented" and
"live-confirmed" diverge often enough that no implementation phase should start from the snapshot
alone. This phase is verification only — no production code changed.

## Method

Read-only (and minimally-mutating, test-workspace-only) `curl` calls against the real NAMS SaaS
(`https://memory.neo4jlabs.com/v1`), authenticated with the dev/test workspace's `NAMS_API_KEY` +
`NAMS_DEV_WORKSPACE_ID` (same credentials `NamsLiveFixture`/`LiveNamsFactAttribute` already gate on —
never production). One throwaway conversation was created and deleted afterward
(`DeleteConversationAsync`'s existing idempotent delete, confirmed again here); one already-existing
test entity ("Concurrency test B", a leftover from an earlier live test run) had feedback set on it —
a non-destructive, idempotent write, consistent with this project's standing convention that live
tests never delete data they don't own.

## Findings

| Area | Endpoint | Documented (pinned snapshot) | Live-confirmed | Divergence? |
|---|---|---|---|---|
| Conversation lifecycle | `GET /conversations?limit=` | `{conversations: [...]}`, metadata-only, title/firstMessageSnippet | Exact match — returned real conversations from prior phases (10a-10d), with `agentMemory*` metadata intact | None |
| Context/Observations | `GET /conversations/{id}/observations?limit=` | `{observations: [...]}` | Exact match — `{"observations":[]}` on a fresh conversation (observations are worker-generated after message-window accumulation, so empty is expected here) | None |
| Entity feedback | `PUT /entities/{id}/feedback` | `{userScore, confirmed}` in, `{id, updated: true}` out | Exact match | None |
| Entity graph | `GET /entities/graph` | `{nodes: [...], edges: [...]}`, nodes `additionalProperties` (untyped) | Nodes are **flat entity records**: `{id, name, type, description, confidence, sourceStage, createdAt, updatedAt}`. Edges: `{id (compound "srcId\|TYPE\|tgtId"), sourceId, targetId, type, legacyType, confidence, method, predicate, sourceMessageCount}` | Real shape is richer/more specific than the untyped snapshot — no conflict, just needs concrete domain types |
| Graph expand | `POST /graph/expand` | `{nodes, edges, truncated}` | **Nodes here are NOT the same shape as `/entities/graph`'s nodes** — they're generic graph nodes: `{id, labels: [...], properties: {...}}`, since expand can pull in non-Entity nodes (confirmed: a `Message` node appeared in one expansion, with `labels: ["Message"]` and message-specific `properties`). Edges match the same shape as `/entities/graph`. `truncated: {nodeId, shown, total}` | **Real divergence, not just under-specification**: `GetEntityGraphAsync` and `ExpandGraphAsync` need two distinct node DTOs, not one shared `NamsGraphNode` type |
| Reasoning: record step | `POST /reasoning/steps` | `{conversationId, reasoning, actionTaken, result?}` in, adds `id` out | Exact match | None |
| Reasoning: list steps | `GET /reasoning/steps?conversation_id=` | `{steps: [...]}` | Exact match, plus a `createdAt` timestamp not in the request echo | None (additive) |
| Reasoning: record tool call | `POST /reasoning/tool-calls` | `{stepId?, toolName, input, output?, status?, durationMs?}` in (input/output are JSON-**encoded strings**, not objects) | Exact match, including the string-encoding requirement | None |
| Reasoning: trace | `GET /reasoning/trace/{conversationId}` | `{conversationId, steps: [...], toolCalls: [...]}` (flat, not nested) | Exact match — confirmed flat, not steps-with-nested-toolCalls despite the summary text implying nesting | None (the snapshot's own schema was already flat; only the prose description was misleading) |
| Reasoning: provenance | `GET /reasoning/provenance/{entityId}` | `{entityId, steps: [...]}` per `EntityReasoningProvenanceResponse` | **Field is actually named `provenance`, not `steps`**: `{"entityId":"...","provenance":[]}` | **Real divergence** — the pinned snapshot's schema name is wrong; must map from `provenance`, not `steps` |
| Cypher query | `POST /query` | `{cypher, params?}` in; `{columns, rows, stats}` out; writes rejected | Read query (`MATCH (n) RETURN count(n)`) succeeded with exact shape, including a full `stats` object (nodesCreated/nodesDeleted/relationshipsCreated/relationshipsDeleted/propertiesSet/labelsAdded/labelsRemoved/skipped, all zero for a read). A `CREATE` write attempt was rejected: **HTTP 400**, `{"error":"write operations are not permitted via this endpoint"}` | None on shape; **confirms the read-only guarantee is real and server-enforced**, not just documentation |

### Infrastructure note (not a gap)

All calls required the `X-Workspace-Id` header for this API key to see any data (an admin/account-scoped
key, not a workspace-scoped one). This is **already handled** by existing plumbing —
`NamsClientFactory.ConfigureHttpClient` already adds this header whenever `NamsOptions.WorkspaceId` is
set, and `NamsLiveFixture` already sets it from `NAMS_DEV_WORKSPACE_ID`. No new client work needed here;
noted only because a raw `curl` probe without the header silently saw zero data, which would have been
a confusing false negative if not caught.

## Revised implementation plan for 10f–10i

The original sizing (see `neo4j-meeting-2026-07-nams-priority.md`, "Effort sizing" section) holds up
well; two adjustments based on confirmed live shapes:

1. **10g (entity feedback + graph)** needs **two** graph node DTOs, not one — `NamsGraphNode` (flat,
   for `GetEntityGraphAsync`) and `NamsExpandNode` (labels + properties bag, for `ExpandGraphAsync`).
   Slightly more domain-model surface than assumed, still a small addition.
2. **10h (reasoning/provenance)** must map `EntityReasoningProvenanceResponse`'s wire field as
   `provenance`, not `steps` (the pinned snapshot's definition name was simply wrong) — a one-line
   correction to make now rather than discover mid-implementation via a failing live test.
3. **10i (Cypher console)** is confirmed as low implementation risk (shape matches exactly, and the
   read-only enforcement is real, defense-in-depth server-side) — the remaining open question is purely
   the product/security decision of whether to expose it at all, not anything technical. Still gated
   behind explicit user approval per the standing plan.

No other divergences surfaced. All five capability areas are confirmed implementable against the real
NAMS SaaS with the shapes above. Proceeding to Phase 10f.
