# Implementation Plan — Upstream TCK HTTP Bridge (Bronze) + `SCN-*` Scenario Mapping

**Status:** ✅ **EXECUTED (2026-07-11).** This plan was carried out and the scope was **expanded to the full
Bronze tier**. The 9 Bronze short-term endpoints were built as planned, and **3 Bronze schema-tier long-term
create endpoints** (`add_entity`, `add_preference`, `add_fact`) were added on top of them — the TCK `bronze`
marker is defined as *"schema and short-term memory"*, so its schema tests assert the round-tripped shape of
created entities/facts/preferences and cannot pass without those long-term create endpoints. The bridge now
serves **12 endpoints**. Long-term records are embedded via the deterministic `StubEmbeddingGenerator` and
default `Confidence` to `1.0`. **Result: full Bronze conformance, 93/93 upstream scenarios passing.** Remaining
`⚠️ confirm` markers below have been resolved against the real upstream contract — see the annotations in §5/§6.
The prose that follows is preserved as an as-designed record; the annotations mark what changed at execution time.
**Branch:** `codex/tck-bridge-scn-mapping` (still uncommitted; next step = open the PR into `main`).
**Date:** 2026-07-11.
**Scope:** Bronze tier — the 9 short-term-memory endpoints **plus** the 3 schema-tier long-term create endpoints
required by the Bronze "schema" tests. Silver/Gold/Platinum (long-term search/reasoning/relationship endpoints)
are explicit follow-ups, not part of this slice.

> This plan turns the pause/resume marker in [`../DOING-RIGHT-NOW.md`](../DOING-RIGHT-NOW.md) into a concrete,
> file-by-file build. All facts below were verified against the code on 2026-07-11. Where a fact is marked
> **⚠️ confirm**, run the one-line check named before hard-coding it.

---

## Conformance result (2026-07-11)

Ran the real upstream Technology Compatibility Kit against the live .NET bridge:

```bash
pytest -m bronze --bridge-url http://localhost:3001
```

against `neo4j-labs/agent-memory-tck` at commit **`4603b91f`** (`main`), driving `tools/AgentMemory.TckBridge`
over HTTP against a live **Neo4j 5.26** (Docker). **Result: 93 passed, 0 failed** (96 deselected = the
Silver/Gold/Platinum scenarios). This is the **full Bronze tier**.

Because the upstream runner uses its **own** Pydantic models on **both** ends of the wire (unlike the in-process
mirror tests, which used the bridge's own DTOs and so structurally could not catch a contract mismatch), it
surfaced **five real contract defects** (all now fixed — resolutions inline in §2 and checked off in §6) plus a
shared latent product-test bug:

1. **`TckSessionInfo` shape was wrong.** It carried `{session_id, conversation_count, message_count,
   last_message_preview, last_activity}`; the TCK `TCKSessionInfo` model requires
   `{session_id, message_count, created_at, updated_at}` and reads `created_at` as a **required** key. Corrected
   the DTO + mapping. (The plan's "supersets are safe" guess in §2.3 was **wrong** — see the annotation there.)
2. **Invalid Cypher in the vector-index readiness poll.** `SHOW INDEXES WHERE ... RETURN count(*)` is missing a
   `YIELD` (a Neo4j 5.x syntax error) — the `catch` swallowed it, so the poll silently burned its **full**
   timeout on every call (30s in `/setup`, 60s in the test fixture). Present in **both** the bridge **and**
   `Neo4jIntegrationFixture.WaitForVectorIndexesAsync` (a genuine latent product-test bug). Fixed both with
   `SHOW INDEXES YIELD type, state WHERE ...`. Integration compatibility run dropped from ~1m52s to ~22s.
3. **`delete_message` id-format mismatch.** `IIdGenerator` stores ids as unhyphenated 32-char hex ("N" format),
   but the Python runner round-trips ids through `UUID()` and re-emits canonical dashed form, so `delete_message`
   looked up the dashed id, matched nothing, and returned `False`. Fixed by normalizing the incoming id to "N"
   format in the handler.
4. **`add_fact` request field is `obj` (not `object`).** The request DTO property was named `Object` (→ `object`
   under snake_case), so it never bound and the fact object arrived `null`, failing the Neo4j MERGE. Renamed the
   request property to `Obj`.
5. **`get_conversation` on an unknown session** returned the raw `session_id` as the envelope `id`, which the
   runner parses via `UUID()`; TCK session ids are not UUIDs (fixture: `f"tck-{uuid4()}"`), so this threw. Fixed
   to fall back to the nil UUID (`Guid.Empty`).

Also: `/setup` now returns `{"ok": true}` (matching the upstream C# reference conformance server) instead of
`{"status":"ok"}`.

**Full verified state (2026-07-11):** full solution build 0 warnings / 0 errors; full unit suite **2684/2684**
passing (`TckBridgeWireContractTests` now 17 tests, including new entity/fact/preference DTO field-name locks and
an `add_fact` "obj"-binding regression test); compatibility integration tests **13/13** passing against
Testcontainers Neo4j (including a new `TckBridgeHttpRoundTripTests` `WebApplicationFactory` end-to-end test and
the fixture query fix); upstream Bronze TCK **93/93**.

**Still open / not done:** Silver/Gold/Platinum bridge tiers (the long-term search/reasoning/relationship
endpoints) remain future follow-up slices.

---

## 0. Goal & definition of done

Make the local "mirrored compatibility" evidence *canonical* by letting the upstream `neo4j-labs/agent-memory-tck`
runner drive this .NET implementation out-of-process, and give reviewers traceability from every local mirror to
the stable upstream `SCN-*` scenario IDs.

**Done when:**
1. `tools/AgentMemory.TckBridge` builds 0-warning in Release and serves the Bronze endpoints over HTTP on
   `http://localhost:3001` (9 short-term as originally planned; expanded to 12 at execution — see the status header).
2. `CompatibilityScenarioCatalog` carries upstream `SCN-*` IDs, and catalog guards enforce mapping validity.
3. New unit tests pass (catalog guards + bridge DTO serialization); existing TCK-mirror/catalog tests still pass.
4. Docs updated (`behavioral-compatibility-pack-status.md`, `compatibility-automation.md`, `DOING-RIGHT-NOW.md`).
5. (Optional, env-gated) `pytest -m bronze --bridge-url http://localhost:3001` passes against a live Neo4j.
6. PR opened from `codex/tck-bridge-scn-mapping` into `main`, narrowly scoped to bridge + mapping.

**Non-goals for this slice:** Silver/Gold/Platinum endpoints; MCP HTTP transport; weakening any .NET
owner/store isolation; enumerating SCN-S/G/P IDs.

---

## 1. Verified ground truth (do not re-derive)

### 1.1 Upstream bridge protocol (source: `neo4j-labs/agent-memory-tck` `tck/adapters/base_adapter.py` + `docs/reference/bridge-protocol.adoc`, corroborated by `DOING-RIGHT-NOW.md`)
- Routing: **`POST /{snake_case_method}`**, method names match the adapter methods exactly.
- Request body: **flat JSON object**; `null`/absent optional fields are **omitted** from the request.
- Response encoding: **UUID strings**, **ISO-8601** datetimes, **lowercase enum strings**, JSON objects/lists/nulls.
- Default bridge listen URL: **`http://localhost:3001`**. Runner invoked as `pytest -m bronze --bridge-url http://localhost:3001`.
- Bronze short-term endpoints (this slice): `setup`, `teardown`, `clear_all_data`, `add_message`,
  `get_conversation`, `search_messages`, `list_sessions`, `delete_message`, `clear_session`.

### 1.2 csproj / build conventions (source: `Directory.Build.props`, `tools/AgentMemory.Cli/AgentMemory.Cli.csproj`)
- **No central package management** for `src`/`tools` (the only `Directory.Packages.props` lives under the
  vendored `Neo4j/neo4j-maf-provider/` tree and does **not** govern this project). Pin `PackageReference`
  `Version`s inline — but the bridge needs **no** package refs (see §2.1).
- `Directory.Build.props` **inherits** into every project: `net9.0`, `Nullable=enable`, `ImplicitUsings=enable`,
  `LangVersion=latest`. **Do not redeclare these.**
- `TreatWarningsAsErrors=true` auto-applies to any project whose name does **not** contain `.Tests` → the bridge
  **must compile 0-warning**.
- **Packaging gotcha:** `Directory.Build.props` auto-applies publishable NuGet metadata to every project whose
  name `StartsWith("AgentMemory")` && `!Contains(".Tests")` && `!Contains(".Sample")`. `AgentMemory.TckBridge`
  matches → it would be treated as a shippable package. **Set `<IsPackable>false</IsPackable>`** (the CLI does
  exactly this).

### 1.3 DI entry point (source: `src/AgentMemory/ServiceCollectionExtensions.cs`)
Use the **meta-package overload** (`using AgentMemory;`), the same one `TckMirroredBehaviorTests` uses:
```csharp
services.AddNeo4jAgentMemory(
    configureMemory: _ => { },                              // MemoryOptions (AgentMemory.Abstractions.Options)
    configureNeo4j:  o => { o.Uri=...; o.Username=...; o.Password=...; o.Database="neo4j"; o.EmbeddingDimensions=dims; });
// configureLlm left null ⇒ memory-only, Core no-op extractors, NO IChatClient required.
```
This overload internally calls `AddAgentMemoryCore` and registers `IClock`/`IIdGenerator` defaults, so you do
**not** register them manually (unlike the McpHost sample, which uses the lower-level Neo4j overload).

`Neo4jOptions` (namespace `AgentMemory.Neo4j.Infrastructure`) defaults: `Uri="bolt://localhost:7687"`,
`Username="neo4j"`, `Password="password"`, `Database="neo4j"`, `EmbeddingDimensions=1536`,
`ValidateVectorIndexDimensions=true`. `ValidateOnStart` throws on bad config at host start.

### 1.4 Schema bootstrap is NOT auto-run (source: `src/AgentMemory.Neo4j/Infrastructure/ISchemaBootstrapper.cs`)
`AddNeo4jAgentMemory` registers `ISchemaBootstrapper` but does not run it. `/setup` must resolve
`ISchemaBootstrapper` and call `await BootstrapAsync(ct)`. The integration fixture then polls
`SHOW INDEXES` until all VECTOR indexes are `ONLINE` before use — replicate a short readiness wait in `/setup`
(vector index population is asynchronous in Neo4j). Reference: `Neo4jIntegrationFixture` (`tests/.../Fixtures/`).

### 1.5 Full-graph wipe has no service method (source: `Neo4jIntegrationFixture.CleanDatabaseAsync`, lines 77–81)
`clear_all_data` and `teardown` need `MATCH (n) DETACH DELETE n` run on an `AsyncSession`. There is **no**
high-level service for a full wipe. Resolve the Neo4j driver (`INeo4jDriverFactory` → `IDriver`, **⚠️ confirm
the factory method name**) or `INeo4jSessionFactory` and run the Cypher directly, mirroring the fixture.

### 1.6 Domain shapes (source: `src/AgentMemory.Abstractions/Domain/ShortTerm/*.cs`)
- **`Message`** (`sealed record`): `required string MessageId; required string ConversationId; required string SessionId;
  required string Role; required string Content; required DateTimeOffset TimestampUtc; float[]? Embedding;
  IReadOnlyList<string>? ToolCallIds; IReadOnlyDictionary<string,object> Metadata` (empty default). **All 6
  `required` members must be set.**
- **`Conversation`** (`sealed record`): `required string ConversationId; required string SessionId; string? UserId;
  required DateTimeOffset CreatedAtUtc; required DateTimeOffset UpdatedAtUtc; string? Title; bool Archived;
  IReadOnlyDictionary<string,object> Metadata`.
- **`SessionSummary`** (positional record): `SessionSummary(string SessionId, int ConversationCount,
  int MessageCount, string? LastMessagePreview, DateTimeOffset? LastActivity)`.

### 1.7 Short-term API surface (source: `IShortTermMemoryService`, `IConversationRepository`, `IMessageRepository`)
- `IShortTermMemoryService.AddConversationAsync(convId, sessionId, userId?, metadata?, ct)`
- `IShortTermMemoryService.AddMessageAsync(Message, ct)`
- `IShortTermMemoryService.GetConversationMessagesAsync(convId, ct)` (chronological, oldest-first)
- `IShortTermMemoryService.GetAllSessionMessagesAsync(sessionId, ct)` (chronological, no cap)
- `IShortTermMemoryService.GetRecentMessagesAsync(sessionId, limit?, ct)` (**newest-first — do NOT use for `get_conversation`**)
- `IShortTermMemoryService.SearchMessagesAsync(sessionId?, float[] queryEmbedding, limit=10, minScore=0.0, ct)`
- `IShortTermMemoryService.ClearSessionAsync(sessionId, ownerId=null, ct)`
- `IConversationRepository.GetBySessionAsync(sessionId, ct)` → `IReadOnlyList<Conversation>` (**confirmed exists**)
- `IConversationRepository.GetByIdAsync(convId, ct)` → `Conversation?`
- `IConversationRepository.ListSessionsAsync(limit=50, ct)` → `IReadOnlyList<SessionSummary>`
- `IMessageRepository.DeleteAsync(messageId, cascade=true, ct)` → `bool`

### 1.8 Stub embedding (source: `src/AgentMemory.Core/Stubs/StubEmbeddingGenerator.cs`)
`public StubEmbeddingGenerator(ILogger<StubEmbeddingGenerator> logger, int dimensions = 1536)` — deterministic
random vectors seeded by `text.GetHashCode()` (same input → same vector). **Register with `dimensions` equal to
`Neo4jOptions.EmbeddingDimensions`** or vector-index bootstrap throws a dimension-mismatch exception. Recipe
(from `TckMirroredBehaviorTests` lines 48–51):
```csharp
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
    new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), dims));
```

### 1.9 No ASP.NET host exists yet
The bridge is the **first** `Microsoft.NET.Sdk.Web` project in the repo (`McpServer` is a class library; the MCP
*host* is a generic-host console sample). Mirror the McpHost sample's service-registration block but swap
`Host.CreateApplicationBuilder(args)` → `WebApplication.CreateBuilder(args)`.

---

## 2. Part A — the bridge project (`tools/AgentMemory.TckBridge`)

### 2.1 `tools/AgentMemory.TckBridge/AgentMemory.TckBridge.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>AgentMemory.TckBridge</RootNamespace>
    <!-- Operator/conformance tooling, not a published library. -->
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Meta-package transitively exposes Abstractions + Core + Neo4j + extraction + observability. -->
    <ProjectReference Include="..\..\src\AgentMemory\AgentMemory.csproj" />
  </ItemGroup>
</Project>
```
- Do **not** declare `TargetFramework`/`Nullable`/`ImplicitUsings` (inherited).
- The Web SDK supplies the ASP.NET Core framework reference implicitly → **no** `PackageReference` needed for
  `WebApplication`/minimal API.
- Add the project to the root solution (`AgentMemory.slnx`). **⚠️ confirm** the exact solution filename at repo
  root and whether `tools/*` projects are members (the CLI is; follow that pattern).

### 2.2 `Program.cs` — host + config + JSON + schema bootstrap
Wiring order and the non-obvious bits:

1. `var builder = WebApplication.CreateBuilder(args);`
2. **Config conventions** (mirror CLI/McpHost): read `Neo4j:Uri|Username|Password|Database` with fallbacks
   `bolt://localhost:7687` / `neo4j` / `password` / `neo4j`. ASP.NET binds `Neo4j__*` and (with the default env
   provider) `NEO4J_*`-style vars automatically. Read `EmbeddingDimensions` (default 1536) into a local `dims`.
3. **Default listen URL** `http://localhost:3001`: set `builder.WebHost.UseUrls("http://localhost:3001")` unless
   `ASPNETCORE_URLS` is already set (let an explicit env var win).
4. **Register services** (same block as McpHost, meta overload):
   ```csharp
   builder.Services.AddNeo4jAgentMemory(
       configureMemory: _ => { },
       configureNeo4j: o => { o.Uri = uri; o.Username = user; o.Password = pass; o.Database = db; o.EmbeddingDimensions = dims; });
   builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
       new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), dims));
   ```
   > **⚠️ confirm** whether `AddNeo4jAgentMemory` already `TryAdd`s a `StubEmbeddingGenerator`; if so this
   > explicit registration is still fine (last-wins / TryAdd no-ops). If a real embedding provider is ever
   > injected via config, resolve it instead — but Bronze conformance only needs determinism.
5. **JSON contract** — the protocol is snake_case + lowercase enums; ASP.NET defaults to camelCase, so override:
   ```csharp
   builder.Services.ConfigureHttpJsonOptions(o =>
   {
       o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
       o.SerializerOptions.PropertyNameCaseInsensitive = true;                       // tolerate request casing
       o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
       // Do NOT globally ignore nulls: responses legitimately carry embedding:null / title:null.
   });
   ```
   Roles are plain strings in the domain, so no enum conversion is needed for `role`; keep lowercase by passing
   the request value through unchanged.
6. `var app = builder.Build();`
7. **`/setup` bootstrap**: endpoints call `ISchemaBootstrapper.BootstrapAsync` on demand (see §2.4). Do not run
   it at startup — the runner calls `/setup` explicitly, and startup-bootstrap would fight `ValidateOnStart` if
   the DB is unreachable at boot.
8. Map the 9 routes (§2.4). `app.Run();`

### 2.3 DTOs (`Dtos.cs`) — wire contract
Use `record` types; property names are C# PascalCase and become snake_case via the naming policy.
```csharp
// Responses
public sealed record TckMessage(string Id, string Role, string Content, DateTimeOffset Timestamp,
                                float[]? Embedding, IReadOnlyDictionary<string, object> Metadata);
public sealed record TckConversation(string Id, string SessionId, IReadOnlyList<TckMessage> Messages,
                                     string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record TckSessionInfo(string SessionId, int ConversationCount, int MessageCount,
                                    string? LastMessagePreview, DateTimeOffset? LastActivity);
// Requests (optional fields nullable; omitted-when-null is a request concern only)
public sealed record AddMessageRequest(string SessionId, string Role, string Content,
                                       IReadOnlyDictionary<string, object>? Metadata);
public sealed record GetConversationRequest(string SessionId, int? Limit);
public sealed record SearchMessagesRequest(string Query, string? SessionId, int? Limit, double? Threshold);
public sealed record ListSessionsRequest(int? Limit);
public sealed record DeleteMessageRequest(string MessageId);
public sealed record ClearSessionRequest(string SessionId);
```
> **⚠️ confirm `TckSessionInfo` field names** against a reference bridge server / `bridge-protocol.adoc` before
> finalizing (`session_id` is certain; the summary fields are the best-effort mapping from `SessionSummary`).
> If upstream expects only `session_id`, keep the extra fields — supersets are safe.
>
> **✅ RESOLVED (2026-07-11) — the "supersets are safe" guess was WRONG.** The TCK `TCKSessionInfo` Pydantic
> model requires **`{session_id, message_count, created_at, updated_at}`** and reads `created_at` as a
> **required** key; the extra fields shown above (`conversation_count`, `last_message_preview`, `last_activity`)
> are not part of the contract, and the missing required `created_at`/`updated_at` made the round-trip fail.
> The DTO + mapping were corrected to `TckSessionInfo(string SessionId, int MessageCount, DateTimeOffset
> CreatedAt, DateTimeOffset UpdatedAt)`. Confirmed against `tck/adapters/base_adapter.py`.

Mapping helper:
```csharp
static TckMessage ToDto(Message m) =>
    new(m.MessageId, m.Role, m.Content, m.TimestampUtc, m.Embedding, m.Metadata);
```

### 2.4 Endpoint handlers (all 9)
Each handler is an `app.MapPost("/name", async (Req req, IService svc, CancellationToken ct) => Results...)`.
Status codes follow the protocol (`204 No Content` where the adapter returns `None`).

| Route | Handler logic | Returns |
|---|---|---|
| `POST /setup` | `await schemaBootstrapper.BootstrapAsync(ct);` then optional readiness poll (`SHOW INDEXES` until VECTOR ONLINE, bounded ~30s). **⚠️ Executed:** the poll needed `SHOW INDEXES YIELD type, state WHERE ...` — the planned `... WHERE ... RETURN count(*)` is invalid Cypher (§6). | `200 {"ok":true}` **← corrected from `{"status":"ok"}` to match the upstream C# reference server** |
| `POST /teardown` | Dispose scope / let host lifetime handle the driver; optionally run the full wipe. | `204` |
| `POST /clear_all_data` | Run `MATCH (n) DETACH DELETE n` (see §2.5). Called before each test for isolation. | `204` |
| `POST /add_message` | **Ensure conversation → build Message → persist** (see §2.4.1). | `200 TckMessage` |
| `POST /get_conversation` | Envelope + messages (see §2.4.2). Empty messages for unknown session (SCN-B-045). | `200 TckConversation` |
| `POST /search_messages` | Embed `query` text via the generator; `SearchMessagesAsync(sessionId, vector, limit ?? 10, threshold ?? 0.7)`; map results. | `200 TckMessage[]` |
| `POST /list_sessions` | `ListSessionsAsync(limit ?? 100)` → map each `SessionSummary` → `TckSessionInfo`. | `200 TckSessionInfo[]` |
| `POST /delete_message` | `bool deleted = await messageRepo.DeleteAsync(req.MessageId, cascade:true, ct);` | `200 {"deleted":deleted}` **⚠️** |
| `POST /clear_session` | `await shortTerm.ClearSessionAsync(req.SessionId, ownerId:null, ct);` (owner-agnostic; see §5). | `204` |

> **⚠️ `delete_message` response shape:** `base_adapter.delete_message` returns a bare `bool`;
> `bridge-protocol.adoc` names a `deleted` field. Confirm bare-bool vs `{"deleted":bool}` against a reference
> bridge server; default to `{"deleted": ...}` and adjust if the runner rejects it.
>
> **✅ RESOLVED (2026-07-11):** `{"deleted": bool}` is **CONFIRMED correct** (against `base_adapter.py` +
> `bridge-protocol.adoc` + the `clients/csharp` reference server). Separately, a real defect was found and fixed
> here: `IIdGenerator` stores ids in unhyphenated "N" format, but the runner round-trips ids through `UUID()`
> and re-emits canonical **dashed** form — so the incoming id must be **normalized to "N" format** in the handler
> before lookup, or the delete matches nothing and always returns `False`.

#### 2.4.1 `add_message` (the trickiest — SCN-B-001/B-002/B-008)
Upstream auto-creates/reuses one conversation **per session** and the **server** assigns `id` + `timestamp`.
```csharp
var convs = await conversationRepo.GetBySessionAsync(req.SessionId, ct);
var conv  = convs.Count > 0 ? convs[0] : null;                     // 1 conversation per session (Bronze assumption)
string conversationId;
if (conv is null)
{
    conversationId = idGenerator.NewId();                          // UUID string (IIdGenerator / GuidIdGenerator)
    await shortTerm.AddConversationAsync(conversationId, req.SessionId, userId: null, metadata: null, ct);
}
else { conversationId = conv.ConversationId; }

var vector = (await embeddingGen.GenerateVectorAsync(req.Content, cancellationToken: ct)).ToArray(); // ⚠️ see note
var message = new Message
{
    MessageId = idGenerator.NewId(),
    ConversationId = conversationId,
    SessionId = req.SessionId,
    Role = req.Role,                                               // already lowercase from upstream
    Content = req.Content,
    TimestampUtc = clock.UtcNow,                                   // IClock (SystemClock)
    Embedding = vector,
    Metadata = req.Metadata ?? new Dictionary<string, object>(),
};
var saved = await shortTerm.AddMessageAsync(message, ct);
return Results.Ok(ToDto(saved));
```
> **⚠️ embedding call:** confirm the exact MEAI invocation the codebase uses for a single string — see
> `src/AgentMemory.Core/.../EmbeddingOrchestrator.cs`. `GenerateVectorAsync(string)` → `ReadOnlyMemory<float>`
> is the MEAI 10.x extension; `.ToArray()` yields the `float[]` the domain wants. Reuse the codebase pattern to
> avoid an API-shape mismatch.
> **⚠️ `IIdGenerator` member name:** confirm it's `NewId()` (vs `Generate()`); it's in `AgentMemory.Abstractions.Services`.

#### 2.4.2 `get_conversation` (SCN-B-043/B-044/B-045/B-046)
```csharp
var convs = await conversationRepo.GetBySessionAsync(req.SessionId, ct);
var conv  = convs.Count > 0 ? convs[0] : null;
IReadOnlyList<Message> msgs = await shortTerm.GetAllSessionMessagesAsync(req.SessionId, ct); // oldest-first, no cap
if (req.Limit is int lim) msgs = msgs.Take(lim).ToList();          // apply limit AFTER ordering
var dto = new TckConversation(
    Id: conv?.ConversationId ?? req.SessionId,                     // ⚠️ if no conv, upstream still wants an envelope
    SessionId: req.SessionId,
    Messages: msgs.Select(ToDto).ToList(),
    Title: conv?.Title,
    CreatedAt: conv?.CreatedAtUtc ?? default,
    UpdatedAt: conv?.UpdatedAtUtc ?? default);
return Results.Ok(dto);
```
> **⚠️** Confirm what upstream expects for `get_conversation` on an unknown session — an empty-`messages`
> envelope vs a 404. SCN-B-045 ("returns empty for non-existent session") implies the empty envelope; verify
> the `id`/`created_at` expectations for that case.
>
> **✅ RESOLVED (2026-07-11):** the **empty-`messages` envelope** shape is **CONFIRMED** (no 404). But the
> plan's `Id: conv?.ConversationId ?? req.SessionId` fallback was a **defect**: the runner parses the envelope
> `id` via `UUID()`, and TCK session ids are **not** UUIDs (fixture: `f"tck-{uuid4()}"`), so returning the raw
> `session_id` threw. Fixed to fall back to the **nil UUID (`Guid.Empty`)** when there is no conversation.

### 2.5 Full-graph wipe helper
```csharp
// Resolve IDriver (via INeo4jDriverFactory — ⚠️ confirm method) and run the same Cypher the fixture uses.
await using var session = driver.AsyncSession();
await session.RunAsync("MATCH (n) DETACH DELETE n");
```
Encapsulate in a small `BridgeAdmin` service registered in DI, so `/clear_all_data` and `/teardown` share it.

---

## 3. Part B — `SCN-*` scenario mapping in the catalog

File: `tests/AgentMemory.Tests.Integration/Compatibility/CompatibilityScenarioCatalog.cs`.

### 3.1 Extend the record
```csharp
internal sealed record CompatibilityScenario(
    string Id,
    string Tier,
    string Feature,
    string Mode,
    string Description,
    IReadOnlyList<string> UpstreamScenarioIds); // NEW — stable upstream SCN-* IDs this row mirrors
```
Update all 9 existing entries. Only the Bronze row gets confident IDs this slice; the rest get `[]` with a
`// TODO` (Silver/Gold SCN enumeration is a documented follow-up, not this slice).

### 3.2 Bronze mapping (confidence-rated — verify before hard-coding)
`NET-TCK-B-001` → the six Bronze scenarios its in-process test (`TckMirroredBehaviorTests.NET_TCK_B_001_*`)
actually exercises:

| SCN ID | Confidence | Upstream behavior |
|---|---|---|
| `SCN-B-001` | **rock-solid** (verbatim in 2 fetches) | First message creates conversation node |
| `SCN-B-002` | **rock-solid** (verbatim in 2 fetches) | Subsequent messages reuse existing conversation |
| `SCN-B-043` | single-fetch — **⚠️ verify** | `get_conversation` returns messages in insertion order |
| `SCN-B-044` | single-fetch — **⚠️ verify** | `get_conversation` respects `limit` |
| `SCN-B-055` | single-fetch — **⚠️ verify** | Search finds relevant messages |
| `SCN-B-079` | single-fetch — **⚠️ verify** | `clear_session` removes all messages |

**Verification step (required before committing the IDs):** fetch `tck/registry/scenario_ids.yaml` from
`neo4j-labs/agent-memory-tck` and confirm each number/title. `SCN-B-001`/`002` are confirmed; the other four
came from a single WebFetch summarized through a small model. Bronze tier = `SCN-B-001..SCN-B-093` (93 scenarios;
189 total across Bronze/Silver/Gold/Platinum).

Bronze IDs that are **bridge-reachable but not yet asserted** by the in-process mirror (leave `netMirrorId`
unmapped; candidates for a future mirror-test expansion, not this slice): `SCN-B-003, B-005, B-006, B-008, B-009,
B-045, B-046, B-056, B-057, B-062, B-065, B-070, B-071, B-072, B-080`.

### 3.3 Catalog guards (`CompatibilityScenarioCatalogTests.cs`)
Keep the existing three tests; add:
- **`UpstreamScenarioIds_MatchScnPattern`**: every listed ID matches `^SCN-[BSGP]-\d{3}$`.
- **`UpstreamScenarioIds_AreUniqueAcrossCatalog`**: no SCN ID is claimed by two different rows (dedupe across all
  `UpstreamScenarioIds`).
- **`BronzeUpstreamMirror_HasMapping`**: the `NET-TCK-B-001` row has a non-empty `UpstreamScenarioIds`
  (scope the "non-empty" requirement to Bronze-tier upstream-mirrored rows so Silver/Gold `[]` placeholders
  don't fail the build; note this scoping in a comment referencing the follow-up).

---

## 4. Part C — tests

1. **Catalog guards** (§3.3) — pure unit tests, no Neo4j.
2. **Bridge DTO serialization** (new unit test project or a `tools`-adjacent test — **⚠️ decide placement**;
   simplest is a `[Fact]` in the existing unit test project that serializes a `TckMessage`/`TckConversation`
   with the bridge's `JsonSerializerOptions` and asserts snake_case keys, ISO-8601 timestamp, lowercase enum,
   `embedding:null` preserved). This locks the wire contract without a live server.
3. **(Optional) live bridge integration** via `WebApplicationFactory<Program>` + the Testcontainers Neo4j
   fixture: POST `/setup` → `/add_message` → `/get_conversation` and assert the round-trip. Keep it in the
   integration project under `[Trait("Category","Integration")]`. Marked optional to keep the first PR narrow;
   the in-process `TckMirroredBehaviorTests` already proves the underlying service behavior.

Run existing suites to prove no regression: `TckMirroredBehaviorTests`, `CompatibilityScenarioCatalogTests`.

---

## 5. Intentional divergences / what NOT to do
- **Owner/store isolation stays strict.** `clear_session` upstream has no owner param; messages/conversations are
  owner-agnostic in .NET (only reasoning traces carry `owner_id`), so pass `ownerId: null`. Do **not** weaken
  owner scoping to satisfy an upstream assumption — mark stricter .NET behavior as an intentional divergence.
- Do not claim Silver/Gold/Platinum conformance until those endpoints exist.
- Do not invent SCN IDs. Any ID not verified against `scenario_ids.yaml` must be marked `⚠️ verify` until checked.
- Keep `IsPackable=false` — the bridge is not a shipped package.

> **✅ Held at execution (2026-07-11).** The strict owner-isolation divergence was preserved (`clear_session`
> passes `ownerId: null`; no owner scoping was weakened) and Bronze still went 93/93. No Silver/Gold/Platinum
> conformance is claimed — those endpoints are not built. SCN IDs were confirmed against the live Bronze run
> rather than invented.

---

## 6. Open items to confirm during implementation (checklist)

> **All items below were settled during execution (2026-07-11); resolutions noted inline.** The five that the
> upstream 93/93 conformance run decided are the DTO/handler contract items.

- [x] Exact root solution filename + whether `tools/*` are members; add the project there. — **Done**; bridge
  added to the solution alongside the CLI (`tools/*` members).
- [x] `IDriver`/`INeo4jDriverFactory`/`INeo4jSessionFactory` exact resolution for the full wipe. — **Resolved**;
  full-graph wipe runs `MATCH (n) DETACH DELETE n` on an `AsyncSession` mirroring `Neo4jIntegrationFixture`.
- [x] MEAI single-string embedding call (match `EmbeddingOrchestrator`). — **Resolved**; long-term records
  (entity/preference/fact) are embedded via the deterministic `StubEmbeddingGenerator`, same as short-term.
- [x] `IIdGenerator` member name (`NewId()` vs `Generate()`); `IClock` member (`UtcNow`). — **Resolved.**
  Additionally: `IIdGenerator` emits **unhyphenated "N" format**, so `delete_message` must normalize the runner's
  dashed UUID to "N" format before lookup (defect #3 — fixed).
- [x] `delete_message` response: bare bool vs `{"deleted":bool}`. — **CONFIRMED `{"deleted": bool}`** against
  `base_adapter.py` + `bridge-protocol.adoc` + the `clients/csharp` reference server.
- [x] `TckSessionInfo` exact field names (`bridge-protocol.adoc` / reference server). — **CORRECTED** to
  `{session_id, message_count, created_at, updated_at}` (`created_at` **required**); the earlier "superset is
  safe" guess was wrong (defect #1 — fixed).
- [x] `get_conversation` unknown-session contract (empty envelope vs 404; envelope `id`/timestamps). —
  **CONFIRMED empty-`messages` envelope** (no 404); envelope `id` must fall back to the **nil UUID** (`Guid.Empty`)
  because the runner `UUID()`-parses it and TCK session ids are not UUIDs (defect #5 — fixed).
- [x] Whether `AddNeo4jAgentMemory` already registers a `StubEmbeddingGenerator` (avoid double-register surprise).
  — **Resolved**; explicit registration is fine (last-wins / TryAdd no-op) and dimensions match `EmbeddingDimensions`.
- [x] Confirm `SCN-B-043/044/055/079` numbers/titles against `scenario_ids.yaml`. — **Resolved** during the
  Bronze conformance run (full Bronze tier `SCN-B-001..093`, 93/93).

**Additional items settled by the conformance run (not in the original checklist):**
- [x] **`SHOW INDEXES` readiness poll uses invalid Cypher.** `SHOW INDEXES WHERE ... RETURN count(*)` is missing
  a `YIELD` (Neo4j 5.x syntax error, swallowed by the `catch`), silently burning the full poll timeout every
  call. Fixed with `SHOW INDEXES YIELD type, state WHERE ...` in **both** the bridge **and**
  `Neo4jIntegrationFixture.WaitForVectorIndexesAsync` (integration run ~1m52s → ~22s).
- [x] **`add_fact` request field is `obj`, not `object`.** DTO property `Object` (→ `object` under snake_case)
  never bound → fact object arrived `null` → Neo4j MERGE failed. Renamed the property to `Obj` (defect #4 — fixed).
- [x] **`/setup` response body.** Now returns `{"ok": true}` (matching the upstream C# reference conformance
  server) instead of `{"status":"ok"}`.

---

## 7. Suggested commit / PR sequence
1. `feat(tck): add AgentMemory.TckBridge Bronze HTTP host` — project + `Program.cs` + DTOs + handlers + wipe helper; add to solution.
2. `feat(compat): map local Bronze mirror to upstream SCN-* IDs` — catalog record change + Bronze mapping + guards.
3. `test(tck): bridge DTO serialization + catalog guards` (+ optional live round-trip).
4. `docs(compat): record Bronze bridge command + support tier` — update `behavioral-compatibility-pack-status.md`
   (status table: TCK bridge row → move Bronze from "pending automation" to "verified, Bronze"),
   `compatibility-automation.md` (bridge command + `pytest -m bronze --bridge-url ...`), and
   `DOING-RIGHT-NOW.md` (mark the slice done; note Silver/Gold as next).
5. Open the PR into `main`, narrowly scoped to bridge + mapping; cite the verification results and the intentional
   owner-isolation divergence in the description.

**Local validation before pushing:**
```bash
dotnet build tools/AgentMemory.TckBridge/AgentMemory.TckBridge.csproj -c Release   # 0 warnings
dotnet test tests/AgentMemory.Tests.Unit/AgentMemory.Tests.Unit.csproj --filter FullyQualifiedName~CompatibilityScenarioCatalog
dotnet test tests/AgentMemory.Tests.Integration/... --filter "FullyQualifiedName~TckMirroredBehaviorTests|FullyQualifiedName~CompatibilityScenarioCatalog"
# Optional, env-gated (needs live Neo4j + Python TCK tooling):
#   dotnet run --project tools/AgentMemory.TckBridge & ; pytest -m bronze --bridge-url http://localhost:3001
```
> Reminder (session gotcha): run the **full** unit suite before pushing — a filtered subset only happy-paths your
> own change.
