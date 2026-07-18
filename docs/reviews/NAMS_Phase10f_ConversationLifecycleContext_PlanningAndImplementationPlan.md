# NAMS Phase 10f: Conversation Lifecycle & Context Completion — Planning & Implementation Plan

## Scope

Add the two remaining TCK Platinum conversation-lifecycle/context capabilities that
`INamsClient` does not yet expose:

- `list_conversations` (`GET /conversations`)
- `get_observations` (`GET /conversations/{id}/observations`)

Bulk-add (`AddMessagesAsync`) and delete (`DeleteConversationAsync`) are already shipped (Phase 2 /
Phase 10b); `GetContextAsync` already returns observations as part of the three-tier context bundle.
This phase only adds the two standalone endpoints, at the same "low-level client capability only" tier
as `SearchMessagesAsync`/`DeleteConversationAsync` — deliberately **not** wired into
`INamsPersistenceService`, `INamsConversationResolver`, or any MCP tool. That's a separate decision,
consistent with the Phase 10a/10b precedent.

## Design, informed by the Phase 10e live-probe spike

### `ListConversationsAsync`

Live-confirmed response shape (`docs/reviews/NAMS_Phase10e_PlatinumLiveProbeSpike_FindingsAndPlan.md`)
is **not** the same as the existing `NamsConversation` record (which models `POST /conversations`'s
create-response: `id`, `workspaceId`, `userId`, `metadata`). The list item instead has:
`id`, `userId`, `metadata`, `title`, `firstMessageSnippet`, `messageCount`, `createdAt`, `updatedAt` —
no `workspaceId`. This needs its own record, `NamsConversationSummary`, not a reuse of `NamsConversation`.

```csharp
internal sealed record NamsConversationSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("firstMessageSnippet")] string? FirstMessageSnippet,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("createdAt")] string? CreatedAt,
    [property: JsonPropertyName("updatedAt")] string? UpdatedAt);
```

`ListConversationsAsync(int limit, CancellationToken)` mirrors `ListEntitiesAsync`'s existing shape
exactly (`GET conversations?limit={limit}`, unwraps a `{conversations: [...]}` envelope).

### `GetObservationsAsync`

The live-confirmed `handlers.Observation` shape matches the **already-existing** `NamsObservation`
domain record exactly (it's already used inside `NamsContext.Observations` from `GetContextAsync`) — no
new domain type needed. `GetObservationsAsync(string conversationId, int limit, CancellationToken)`
mirrors `SearchMessagesAsync`'s path-building style (`GET conversations/{id}/observations?limit={limit}`),
unwraps a `{observations: [...]}` envelope into `IReadOnlyList<NamsObservation>`.

### Live-test design for observations — attempted a positive test, empirically proved it too flaky to ship

A throwaway empirical probe (this session, not committed) first attempted to force observation
generation: created a conversation, bulk-added 30 varied messages, then polled both
`GET .../extraction-status` and `GET .../observations` (up to 180s). Findings:

- `extraction-status` confirmed the engineering plan's own Section 4.3 note (line ~483/1114/1137) that
  this is the documented way to observe worker completion — it processes messages **serially**, one at
  a time (`attempts`/`status: pending → processing → done` per message), not in a burst.
- At **t=100s**, observations genuinely appeared: 2 observations, one explicitly covering "messages
  10-29" — suggesting the worker batches in ~20-message windows once extraction catches up.
- Separately, querying the existing highest-message-count conversations already in the dev workspace
  (only up to 8 messages, created hours earlier by the sample app) showed zero observations — consistent
  with a threshold well above 8 messages.

Based on that single successful probe, this phase initially **shipped** a second, slower test
(`GetObservationsAsync_AfterSufficientMessages_ReturnsRealGeneratedContent`) that bulk-added 25 messages
and polled up to 150s asserting real generated content. **Running it for real (not the throwaway probe,
the actual committed test, via `dotnet test --filter`) failed: no observations appeared within the full
150s bound.** The worker's actual timing is evidently not reliably reproducible at this margin — it
may depend on server load, queue depth from other concurrent test/demo traffic in the shared dev
workspace, or a schedule not tightly coupled to message count the way the one successful probe
suggested.

**Conclusion, revised from the design above after hitting a real failure:** this is exactly the
"don't force what can't be deterministically forced" situation the original (pre-probe) instinct
correctly anticipated, and exactly the kind of trap this project's own history repeatedly warns about —
a test that passes SOMETIMES depending on external timing is worse than no test, because it erodes
trust in the suite. The positive test was **removed**. The shipped live test verifies only the
call's wiring and shape (a fresh, single-message conversation legitimately returns `{observations: []}`,
correctly typed, no error) — a genuine (if negative) assertion that proves `GetObservationsAsync`
reaches the real endpoint and deserializes correctly, which is what this test-completeness phase is
for. Forcing a reliable positive-population test would need either a documented, guaranteed trigger
(not available) or a much longer/looser bound that would make the live suite unacceptably slow to run
before every future phase's merge — out of scope for this phase, same as Phase 10b left data-lifecycle
policy decisions to a later, deliberate phase.

## Implementation checklist

1. New domain record `NamsConversationSummary` (`src/AgentMemory.Nams/Domain/`).
2. `INamsClient`: add `ListConversationsAsync(int limit, CancellationToken)` and
   `GetObservationsAsync(string conversationId, int limit, CancellationToken)`, each with a doc comment
   following the established convention (what confirmed it, live vs. plan divergence notes, why not
   wired into higher services yet).
3. `Neo4jNamsClientAdapter`: implement both, following `ListEntitiesAsync`'s GET-with-query-string
   pattern; add the two wire-only envelope records
   (`ConversationsResponseBody`, `ObservationsResponseBody`).
4. Live tests (`tests/AgentMemory.Tests.Integration/Nams/NamsConversationLifecycleTests.cs`, new file,
   `LiveNamsFactAttribute`-gated, `[Trait("Category","Integration")]`):
   - `ListConversationsAsync_ReturnsCreatedConversationsWithCorrectSummaryFields`: create two
     `[CallerMemberName]`-tagged conversations with distinct metadata titles, list, assert both appear
     by id with matching `userId`/`title`/`messageCount` (0, since no messages added) — proves real
     round-trip, not just "list returns something."
   - `GetObservationsAsync_OnFreshConversation_ReturnsEmptyTypedList`: create a conversation, add one
     message (to exercise the endpoint against a real, non-trivial conversation rather than a
     zero-message edge case), call `GetObservationsAsync`, assert it returns an empty
     `IReadOnlyList<NamsObservation>` without throwing. Doc comment records that a positive-generation
     test was attempted and removed after a real failure (see design section above) so a future reader
     doesn't mistake the absence of a positive test for an oversight.
   - Both tests delete their conversations afterward via the existing `DeleteConversationAsync`
     (already idempotent, confirmed Phase 10b) for workspace hygiene, matching this phase's own new
     capability being exercised responsibly.
