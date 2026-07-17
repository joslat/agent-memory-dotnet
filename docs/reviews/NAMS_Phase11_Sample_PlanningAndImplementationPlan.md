# NAMS Phase 11 — Sample — Planning and Implementation Plan

**Prepared:** 2026-07-17
**Branch:** `nams/phase11-sample-nams-agent`
**Purpose:** Executes engineering plan Phase 11 ("Sample and documentation"), scoped to **NAMS only** per
explicit user direction — the plan's own "backend-switching sample" (`AGENTMEMORY_BACKEND=neo4j|nams`) is
deliberately not built here; nothing in the plan makes the sample depend on that.

## 1. What this is

`samples/AgentMemory.Sample.NamsAgent` — the NAMS-backed sibling of the flagship `AgentWithMemory` sample,
demonstrating the complete Phase 4-6 pipeline end to end, live, against the real NAMS SaaS:

1. A `ChatClientAgent` with `NamsMemoryContextProvider` (an `AIContextProvider`).
2. A multi-turn session — each turn persisted to NAMS automatically.
3. Session serialize/restore.
4. Durable cross-session recall — a brand-new session for the same user/application still sees prior memory.
5. Sanitized recall diagnostics via the shared `MemoryTraceChatClient` (reused unmodified — NAMS's memory
   blocks use the same `RecalledMemoryDelimiter` format the direct provider uses).

**Deliberately not included** (per the plan's own phase list, not yet built): MCP tool exposure (Phase 8),
backend-switching (out of scope per this session's explicit direction).

## 2. Design decisions

- **No local embedding generator, schema bootstrapper, or memory tools.** NAMS performs
  extraction/embedding/reflection server-side — none of that exists on the client side for this backend.
- **No `.WithMemoryOwnerScoping(sp)`.** That wrapper exists specifically to keep an ambient `AsyncLocal`
  owner context alive across a *local* tool-calling loop (#90) — `NamsMemoryContextProvider` reads identity
  directly off the session state bag with no ambient context and no local tools, so it's unnecessary here.
- **`AddAgentMemoryFramework()` is still required** (Phase 6's own documented precondition for
  `AddNamsAgentMemoryFramework()`), even though it also registers `Neo4jMemoryContextProvider` and friends
  that this sample never resolves. Verified this is harmless: those are `TryAddScoped` registrations, never
  eagerly resolved, and the Generic Host's default `ValidateOnBuild` is off outside `Development` — confirmed
  by actually running the sample (see §4), not just reasoning about it.
- **The NAMS base URL is hardcoded to `NamsWellKnown.Endpoint`**, not an environment variable. NAMS's own
  ecosystem convention (`NAMS_BASE_URL`, seen in the dashboard's own plugin instructions) excludes the `/v1`
  API-version suffix our client requires on `NamsOptions.Endpoint` — reusing that exact name with a
  different required shape would be a trap for anyone following NAMS's own docs. An advanced user who needs
  a different endpoint edits `Program.cs` directly.

## 3. Wiring into the build

- `AgentMemory.slnx` — added the new project under `/samples/`.
- `.github/workflows/ci.yml`'s "Sample smoke builds" step — added an explicit `dotnet build` line (this
  step is a hardcoded list per sample, not a glob — matches every existing sample's own entry).
- `samples/samples.sln` — deliberately **not** touched: it's missing 6+ of the existing samples already and
  isn't referenced anywhere in CI, so it's stale/unmaintained, not a real manifest.
- Not added to `eng/release-packages.txt` — samples aren't NuGet packages.

## 4. Verification — actually run live, not just built

Ran the sample twice against the real NAMS SaaS (`agent-memory-dotnet-dev` workspace) and real Azure OpenAI,
confirming:

- Conversation creation, message persistence (`POST .../messages/bulk`), and recall
  (`GET .../context` + `POST .../entities/search`) all round-trip correctly — real HTTP traffic logged.
- Session serialize (5944-6085 bytes JSON) → deserialize → continued conversation works.
- A brand-new session (Session B) still triggers recall against the same NAMS conversation history.
- `MemoryTraceChatClient` correctly prints `<recalled_memory category="nams.entity">` blocks with no
  changes needed — confirms Phase 6's reuse of the shared `RecalledMemoryDelimiter` format.

**Genuine finding from the live run** (not a code defect — see the README's new "Known characteristic"
section): in a fast, scripted back-to-back conversation, the model's replies don't always visibly reflect
facts stated moments earlier. Two real NAMS characteristics combine to cause this: (1) asynchronous
server-side extraction can race ahead of an immediate follow-up recall (same eventual-consistency behavior
`NamsLiveConnectivityTests` already bounds with polling), and (2) NAMS's entity tier is NER-style
(people/orgs/locations) and doesn't capture general preferences the way a person's name or employer does. A
short `Task.Delay` was added between scripted turns to reduce (not eliminate) this. This is documented
plainly in the sample's README rather than hidden, and is worth feeding into Phase 10's scenario matrix
later (it's exactly the kind of thing the plan's own "eventual consistency: immediate absence, bounded poll,
completion" mandatory scenario exists to characterize).

## 5. Definition of done

- [x] Sample builds clean (0 warnings).
- [x] Actually run against live NAMS + real Azure OpenAI — twice, both fully completing.
- [x] README documents the design decisions, the switch-to-Neo4j seam, and the honest recall-quality caveat.
- [ ] Self-reviewed, PR opened, CI green, merged to `main`.
