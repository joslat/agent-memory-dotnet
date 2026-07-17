# NamsAgent Sample

The NAMS-backed sibling of [AgentWithMemory](../AgentMemory.Sample.AgentWithMemory/README.md) — the same
canonical Microsoft Agent Framework "agent with memory" shape, but memory lives in the real
[NAMS](https://memory.neo4jlabs.com) SaaS instead of a direct Neo4j connection:

- `NamsMemoryContextProvider` (`AgentMemory.AgentFramework.Nams`) injects recalled memory before each agent
  run and persists the turn after the run — the NAMS equivalent of `Neo4jMemoryContextProvider`.
- `WithMemoryIdentity(...)` stamps application, user, session, and conversation identity into the
  `AgentSession` state bag — the exact same helper the direct Neo4j samples use, since both providers read
  identity the same way.
- Session serialize/restore, and a second session, demonstrate memory continuity beyond one in-memory run —
  because the memory lives in NAMS, not in the MAF session.

## Known characteristic: recall quality in a fast, scripted run

Running this sample live against NAMS surfaced something worth knowing rather than hiding: in a fast,
scripted back-to-back conversation like this one, the model's replies often don't visibly reflect facts
stated just moments earlier. Two real characteristics of NAMS (not bugs in this provider) combine to cause
that:

- **Asynchronous extraction.** NAMS ingests and extracts server-side, not synchronously on write — recall
  that fires immediately after persisting a message can race ahead of NAMS's own indexing (the same
  eventual-consistency behavior `NamsLiveConnectivityTests` bounds with polling). This sample adds a short
  `Task.Delay` between scripted turns for exactly that reason; a real user typing at natural speed doesn't
  need it.
- **Entity extraction favors named entities.** NAMS's entity tier is populated by NER-style extraction
  (people, organizations, locations — "Ada", "Contoso" — not general preferences like "prefers dark mode").
  A stated preference is more likely to surface via the `recentMessages`/reflection tiers than as an
  "entity," so a demo run that only inspects `[memory recalled: nams.entity]` trace lines can look like
  recall "missed" a preference that a longer-lived, real conversation would still have access to via chat
  history.

If your dev/test workspace has been reused across many runs (as ours has), you may also see recalled
entities from unrelated earlier test conversations — NAMS's entity search is workspace-scoped, not
conversation-scoped, so a shared workspace's entity graph accumulates across every conversation ever created
in it. A dedicated workspace per demo, or periodically recreating it, keeps this cleaner.

None of this is a defect in the wiring this sample demonstrates — conversation creation, persistence, and
recall all round-trip correctly against the real service. It's a genuine characteristic worth knowing before
judging recall quality from a short, scripted run.

## What's different from the direct Neo4j samples

- **No local embedding generator, schema bootstrapper, or memory tools.** NAMS performs message ingestion,
  entity extraction, embeddings, and reflection generation server-side, asynchronously — this sample never
  touches any of that locally.
- **No `.WithMemoryOwnerScoping(sp)`.** That wrapper exists to keep an ambient `AsyncLocal` owner context
  alive across a *local* tool-calling loop (#90) — it's only needed by the direct backend's memory tools.
  `NamsMemoryContextProvider` reads identity straight off the session's state bag, with no ambient context
  and no local tools (yet), so the session identity alone is sufficient.
- **No MCP tool surface.** Capability-aware MCP tools for NAMS are engineering plan Phase 8 — not built.
  This sample runs the complete recall/persistence lifecycle with no MCP involvement at all, which is also
  the plan's own required baseline: routine memory must never depend on a model deciding to call a tool.

## Live Providers — No Mocks

This sample calls a **real** Azure OpenAI chat model (via the shared `AgentMemory.Samples.Shared` project,
same as every other sample) and the **real, live NAMS SaaS** — there is no mock `IChatClient`, no offline
NAMS fallback, and no embedding generator to configure (NAMS doesn't need one from the client). The chat
client is wrapped in `MemoryTraceChatClient`, which prints the `<recalled_memory>` blocks
`NamsMemoryContextProvider` injects before each live model call, in light blue — the exact same delimiter
format the direct Neo4j provider uses (`RecalledMemoryDelimiter`, shared via `AgentMemory.Core`), so the
trace client works identically for either backend with no changes.

### Required environment variables

```text
AZURE_OPENAI_ENDPOINT    (required, e.g. https://<resource>.openai.azure.com/)
AZURE_OPENAI_API_KEY     (required — no live-model fallback)
AZURE_OPENAI_DEPLOYMENT  (optional, default gpt-4o-mini)
NAMS_API_KEY             (required — your NAMS SaaS API key)
NAMS_WORKSPACE_ID        (optional — only needed for an account-wide/admin key; a workspace-scoped
                          key already carries its workspace implicitly)
```

Get a NAMS API key from your workspace's dashboard at <https://memory.neo4jlabs.com/dashboard/api-keys>.
**Use a dedicated development/test workspace**, not a production one — this sample creates real
conversations and messages.

The NAMS base URL is not an environment variable here: it's `NamsWellKnown.Endpoint`
(`AgentMemory.Nams`'s public-SaaS constant), since it's the same for every consumer and isn't a secret. If
you need to point at a different NAMS-compatible endpoint, change the `o.Endpoint = ...` line in
`Program.cs` directly.

## Switching to direct Neo4j instead

Nothing about the memory *pattern* changes — only the registration:

```csharp
// NAMS:
builder.Services.AddNamsAgentMemory(o => { o.Endpoint = NamsWellKnown.Endpoint; o.ApiKey = ...; });
builder.Services.AddAgentMemoryFramework();
builder.Services.AddNamsAgentMemoryFramework();
var memoryProvider = sp.GetRequiredService<NamsMemoryContextProvider>();

// Direct Neo4j (see AgentWithMemory):
builder.Services.AddNeo4jAgentMemory(o => { o.Uri = ...; o.Username = ...; o.Password = ...; });
builder.Services.AddAgentMemoryFramework();
var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
```

`WithMemoryIdentity(...)` and the agent-building code are identical either way.

## Related docs

- [Phase 6 write-up](../../docs/reviews/NAMS_Phase6_DedicatedMafProvider_PlanningAndImplementationPlan.md) —
  how `NamsMemoryContextProvider` gates/delimits recalled NAMS content with the same #92 security machinery
  the direct provider uses.
- [NAMS engineering plan](../../strategy/NAMS/AgentMemory_NAMS_Backend_Engineering_Plan_V04.md) — the full
  phase-by-phase plan this sample is Phase 11 of.
