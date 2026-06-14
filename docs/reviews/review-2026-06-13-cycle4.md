# Throat-check review — 2026-06-13 (cycle 4)

**Scope:** adversarial review of the **peripheral packages** not covered by cycles 1–3 — the Semantic Kernel
adapter, Enrichment (Nominatim/Wikimedia HTTP), the Observability decorators, the two Extraction packages
(`Extraction.Llm`, `Extraction.AzureLanguage`), the CLI, and the schema-parity kit. Out of scope (already
reviewed): D1–D7 decay/bitemporal, GDS Analytics, invalidate/supersede, R1/R2 repository isolation, and the
core extraction-pipeline stages. **Method:** 5 dimension scanners (read-only `Explore` agents) →
per-finding adversarial verification (skeptic defaults to *reject*). **36 raw candidates → 6 confirmed.**

The verification pass was notably strict: 30 of 36 candidates were refuted (mostly Enrichment/Extraction/parity
"bugs" that turned out to be already-guarded or non-reachable). The 6 survivors are real but mostly low-impact
telemetry/contract issues — **one genuinely impactful (it broke every CLI command's exit code).**

## Findings (ranked)

| # | Severity | Area | Title | Status |
|---|---|---|---|---|
| 1 | 🟥 High | CLI | Host disposed synchronously over an async-only-disposable singleton → throws → every command exits 1 | ✅ fixed |
| 2 | 🟧 Medium | SK adapter | `recall` exposes a dead `conversationId` param → misleads the LLM (and shadowed `userId` positionally) | ✅ fixed |
| 3 | 🟡 Low | Observability | `GenerateEmbeddingsBatchAsync` not `async` → span closes at ~0ms, disconnected from the work | ✅ fixed |
| 4 | 🟡 Low | Observability | `ExtractFrom{Session,Conversation}Async` spans omit the `memory.user_id` tag | ✅ fixed |
| 5 | 🟡 Low | Observability | `AddMessage{,s}Async` record no duration histogram | ⏸️ deferred (rationale) |

---

### 1 — CLI host disposed synchronously over an async-only-disposable singleton → every command exits 1
**High · `tools/AgentMemory.Cli/Program.cs:67`**

`Neo4jDriverFactory` is registered as a **singleton that implements `IAsyncDisposable` only** (no
`IDisposable`). Every Neo4j CLI verb (`migrate`/`bootstrap`/`consolidate`/`decay`/`conflicts`/`invalidate`/
`supersede`) resolves it. `Program.cs` disposed the host with **`using var host`** (synchronous `Dispose()`).
Per .NET DI, a synchronous `ServiceProvider.Dispose()` that encounters an async-only-disposable it owns does
**not** silently skip cleanup — it **throws `InvalidOperationException`** ("type only implements
IAsyncDisposable. Use DisposeAsync…"). That throw, raised on scope-exit *after* the command succeeded, is
caught by the surrounding `catch` and turned into `error: …` + **exit code 1** — so **every successful CLI
command reported failure** (any CI/script checking `$?` always saw a failure). (The scanner's original
"cleanup silently skipped / pool leaked" framing was wrong — the verifier corrected it to the exit-code break;
severity High, not critical: no data/security impact, the process exits so the OS reclaims sockets.)

**Best fix (applied):** dispose the host via `DisposeAsync`. The entry point is already async (top-level
statements). `IHost` (the static type) only exposes `IDisposable`, but the generic-host implementation is
`IAsyncDisposable`, so: `var host = builder.Build(); await using var hostAsync = (IAsyncDisposable)host;` and
`await using var scope = host.Services.CreateAsyncScope();`. **Tests:** `Neo4jDriverFactoryDisposalTests` —
the factory is async-disposable-only; a provider owning it throws on sync `Dispose()` and succeeds on
`DisposeAsync()` (no DB needed — the driver is lazy).

### 2 — SK `recall` exposes a dead `conversationId` parameter
**Medium · `src/AgentMemory.SemanticKernel/Neo4jMemoryPlugin.cs`**

The `recall` `KernelFunction` declared `conversationId` (`[Description("Optional conversation identifier to
narrow recall scope")]`) but never used it — `RecallRequest` has no `ConversationId` field and the recall
pipeline has no conversation-scoping path. So the SK-surfaced description **promised the LLM a scoping knob
that does nothing**, and, because `conversationId` sat *before* `userId` in the signature, a positional caller
passing an owner id had it silently land in the dead slot (recall ran unscoped). No data leak (recall is still
correctly session-scoped), so Medium, not High — the same "no-leak tool-surface" class as cycle-3 #7.

**Best fix (applied):** remove the dead `conversationId` parameter (and its description). Non-breaking — the
samples call `IMemoryService.RecallAsync` (not the plugin) and the SK tests use 2-arg positional calls. Bonus:
`userId` is now the 3rd positional arg, so a positional owner id correctly reaches `RecallRequest.UserId`.
Implementing *real* conversation scoping (new field threaded through assembler + repo queries) would be
disproportionate for this surface. **Test:** `RecallAsync("q","s1","alice")` now scopes to `UserId=="alice"`.

### 3 — Observability: `GenerateEmbeddingsBatchAsync` span closes before the work runs
**Low · `src/AgentMemory.Observability/InstrumentedMemoryService.cs`**

The method was **not** `async` — it created `using var activity = …Start(…)` then `return _inner.Generate…(…)`.
Because it returned the still-pending `Task` synchronously, the `using` disposed the activity immediately, so
the span closed with ~0ms duration, disconnected from the actual async embedding work (every sibling method is
correctly `async`/`await`). Telemetry-accuracy only — no functional/data impact, hence Low.

**Best fix (applied):** make it `async` and `await` the inner call so the `using` spans the work. **Test:** the
inner delays 60ms; the captured span's `Duration` is asserted `> 20ms` (a sync body would yield ~0).

### 4 — Observability: extraction spans omit the owner tag
**Low · `src/AgentMemory.Observability/InstrumentedMemoryService.cs`**

`ExtractFromSessionAsync` and `ExtractFromConversationAsync` accept an owner-scoping `userId` but never tag it
on the span, so owner context is invisible in traces (correlation gap only — `userId` is still forwarded to
the inner service correctly). Applied uniformly to **both** methods (the scanner flagged only one; they share
the omission).

**Best fix (applied):** `if (userId is not null) activity?.SetTag("memory.user_id", userId);` on both
(guarded so the shared/global single-tenant case emits no empty tag). **Tests:** owner tag present when
`userId` supplied, absent otherwise.

### 5 — Observability: message-write methods record no duration histogram — DEFERRED
**Low · `InstrumentedMemoryService.AddMessageAsync` / `AddMessagesAsync`**

These record the `MessagesStored` counter but no duration histogram. The verifier downgraded both to Low and
recommended *declining*: (a) the **activity span already captures per-operation latency**, so timing isn't
actually "invisible"; (b) **all** write/clear methods here (incl. `ClearSessionAsync`) omit a duration
histogram — it's the existing convention, not an anomaly; (c) the scanner's quick-fix (reuse
`RecallDurationMs`) is **wrong** — it would pollute the recall-latency histogram with write latency.
**Decision: defer.** If write-path duration telemetry is wanted later, add it *uniformly* across the write
methods using the already-declared `PersistDurationMs` (not `RecallDurationMs`). Recorded so the gap is
tracked rather than lost.

---

## Refuted candidates (high-signal "no-action" outcomes)
The 30 refuted candidates included: Enrichment "missing User-Agent / socket exhaustion / unencoded URL" claims
(already handled — `HttpClient` reuse + `Uri.EscapeDataString` + `User-Agent` set), Extraction "silent drop on
malformed JSON" (already wrapped in try/catch that degrades gracefully per the design), and parity-kit
"breaking drift mis-classified as intentional" (the policy gate is structural, not allowlist-only). These are
*confirmations the code is correct*, not misses — logged here so a future cycle doesn't re-flag them.

## Changes in this cycle
- `tools/AgentMemory.Cli/Program.cs` — `await using` host (async disposal) + `CreateAsyncScope` (#1).
- `src/AgentMemory.SemanticKernel/Neo4jMemoryPlugin.cs` — removed dead `conversationId` param (#2).
- `src/AgentMemory.Observability/InstrumentedMemoryService.cs` — `GenerateEmbeddingsBatchAsync` async (#3);
  `memory.user_id` tag on both extraction spans (#4).
- **Tests:** `Neo4jDriverFactoryDisposalTests` (new, 3), `InstrumentedMemoryServiceTests` (+4),
  `Neo4jMemoryPluginTests` (+1). Full suites green: **2453 unit + 34 SK**.

## Follow-ups (not blocking)
- **#5** uniform write-path duration histograms (`PersistDurationMs`) across `AddMessage{,s}`/`ClearSession`
  if/when write-latency telemetry is desired.
