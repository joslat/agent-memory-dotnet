# Production Checklist — Agent Memory for .NET

Operational checklist for deploying AgentMemory in production. Pair this with the
[threat model](threat-model.md) — each item here closes or accepts a specific risk documented there.

AgentMemory has a library-level multi-tenant isolation toggle (`MemoryOptions.Isolation.Mode`, #100), but
it defaults to `SingleTenant` for backward compatibility — it does **not** reject unscoped operations
unless a host explicitly opts in to `StrictMultiTenant`. Isolation still depends on every request path
constructing an explicit `MemoryScope`/owner from your authenticated identity; the toggle is a
fail-closed backstop for the paths that don't, not a substitute for doing so. The first two checklist
items below are how you actually achieve that today.

- [ ] `MemoryOptions.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant` is set for any deployment
      where more than one tenant's data lives in the same store — an omitted owner then throws
      `MemoryOwnerScopeRequiredException` instead of silently falling back to global/shared (see
      [Isolation modes](../getting-started.md#isolation-modes); closes threat-model TT-01/TT-02's
      "no owner at all" half). `WarnOnUnscoped` is a useful intermediate step to find missing call sites
      before flipping to strict. **As of #100 (Stage 1), this only covers the Core recall/extraction/
      reasoning path and 6 MCP entry points** (`memory_entities`, `memory_preferences`,
      `memory_conversations`, `memory_context`, `memory_invalidate`, `memory_supersede`) — several other
      MCP tools that accept a `userId` (entity/fact/preference creation, entity reads, conversation reads)
      are not yet gated by this toggle at all; the next checklist item is not optional just because this
      one is checked.
- [ ] Every request path constructs an explicit `MemoryScope`/`UserId` derived from your authenticated
      host identity — no code path relies on the unscoped/global default (see
      [Owner isolation](../getting-started.md#owner-isolation); closes threat-model TT-01/TT-04). This
      remains necessary even with `StrictMultiTenant` enabled: the mode only catches a *missing* owner,
      not a *wrong* one supplied by an untrusted caller.
- [ ] Owner identity comes from the authenticated host, never from a model or MCP-supplied argument
      (closes TT-02).
- [ ] MCP resources/tools that accept a `userId` parameter are either not exposed to untrusted MCP
      clients, or the host overrides/validates that parameter server-side before the request reaches
      AgentMemory (closes TT-02).
- [ ] `AgentMemoryMcpOptions.EnableGraphQuery` is **off** unless the caller is fully trusted (e.g. an
      internal admin tool) — it executes arbitrary caller-supplied Cypher with no scoping or limits
      (closes TT-08).
- [ ] Administrative operations (`MemoryScope.Global`, cross-owner reads/writes) are not exposed to
      tenant-facing agents or MCP clients.
- [ ] Neo4j uses TLS and least-privilege credentials — this is also your only protection against
      audit-record tampering (TT-11); AgentMemory does not enforce this itself.
- [ ] Secrets (Neo4j credentials, LLM/embedding provider API keys) are stored outside configuration
      files (a secrets manager, environment variables from a secure source, etc.).
- [ ] A real embedding provider is configured — the built-in `StubEmbeddingGenerator` returns
      deterministic random vectors and is suitable only for wiring/structure tests, never production.
- [ ] Retention, invalidation, and hard-deletion policies are defined for long-term memory and audit
      data.
- [ ] Audit data (`:MemoryReadAudit`) has a retention and access policy of its own (closes TT-11).
- [ ] LLM/embedding provider data-processing terms have been reviewed for the conversation and
      extraction content your deployment will send them (closes TT-10).
- [ ] Rate limiting is enforced at your host/API-gateway layer. AgentMemory implements **no per-owner
      request throttling anywhere** — the only rate limiting in the codebase throttles outbound calls
      to third-party geocoding APIs, which is unrelated. Context budgets (`ContextBudget`) only control
      how much recalled memory fits into an LLM prompt; they are not a DoS/rate-limiting mechanism.
- [ ] Backup and restore procedures for Neo4j have been tested.
