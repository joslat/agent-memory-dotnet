# Migrating to the 2.0 candidate

This guide describes the 2.0 feature branch. It does not announce a published release.
The selected writer, ordinary retrieval gate and Recommended composition are supported APIs
in this candidate. Their configuration still belongs to the host.

## What changes, and what stays explicit

New conversational sample and coding-host configurations prefer the selected storage and retrieval
behavior. Existing low-level registrations and the named Conversational preset retain their
compatibility defaults. Upgrading packages alone does not discover a local model, activate an
online judge from an environment key, or rewrite an existing graph.

| Surface | 2.0 candidate policy |
|---|---|
| Store-aware writer, named replacements, closing fallback and writer options | Stable API; previous `AMWRITE001` suppressions are unnecessary. |
| Recommended core/LLM/Agent Framework composition and tier reporting | Stable API; previous `AMREC001` suppressions are unnecessary. |
| Ordinary gate, System One endpoints, update judge, diagnostics and registration | Stable API; ordinary usage no longer requires `AMGATE001` suppression. |
| Compound planner, its request/trace, compound options and planner-taking assembler constructor | Still experimental (`AMGATE001`), off by default and not enabled by Recommended. |
| Optional dreaming cleanup | Separate experimental capability; not enabled by Recommended. No Dreaming for Meaning default. |

API stability concerns the contract, not a guarantee that a model will remember every fact or
judge every candidate correctly. Gate scores describe relevance under that judge's setup; they
are not calibrated answer-correctness probabilities. See [routing](routing.md) for measured scope.

## Adopt the conversational defaults deliberately

Apply the composition in all the packages your host uses. Register the host's chat and embedding
providers separately; the following configuration neither installs nor discovers them.

```csharp
builder.Services.AddNeo4jAgentMemory(
    MemoryOptions.CreateRecommended(),
    neo4j => { /* your connection configuration */ },
    llm => llm.ApplyRecommended());
builder.Services.AddAgentMemoryFramework(adapter => adapter.ApplyRecommended());
builder.Services.AddAgentMemoryGate(gate =>
{
    gate.ApplyRecommended();
    gate.Judges.Add(new SystemOneEndpoint
    {
        Name = "jev",
        Endpoint = new Uri("https://api.typesafe.ai/v1/systemone"),
        KeyVariable = "TYPESAFE_API_KEY",
        Weight = 0.8,
    });
    gate.Judges.Add(new SystemOneEndpoint
    {
        Name = "laya",
        Endpoint = new Uri("http://127.0.0.1:8765/v1/systemone"),
        Weight = 0.2,
    });
    gate.UpdateJudgeName = "jev";
});
```

Configure only endpoints you intend to use; omit LAYA if it is not hosted. Retrieval weights are
normalized over permitted judges that answer. LAYA's retrieval training does not qualify it as a
storage-closing judge, so the example names JEV explicitly for that separate role. A different
compatible local endpoint is configurable, but inherits no accuracy claim from the measured models.

Recommended enables the single-user-turn writer, judged named closings, bitemporal changes and
supersession notes. It disables automatic query splitting. Explicit `RecallRequest.SubQueries`
still work. The Agent Framework part clears question-turn deferral so a retained question is not
later joined to another user turn and dispatched through the batch extractor path.

Set owner/session identity and owner-scoped tools as described in [getting started](getting-started.md).
Apply application overrides **after** the preset. Semantic Kernel uses the same core/gate service
registrations; it does not install another memory engine.

## Fallbacks are configuration, not hidden provider discovery

| Condition | Result |
|---|---|
| One configured retrieval judge fails | Remaining answering judges are used, with normalized weights and diagnostic trace. |
| No permitted retrieval judge answers | Similarity-floor retrieval with a visible fallback reason; recovery respects cooldown and cancellation. |
| External closing judge is missing or fails | The host chat model can judge writer-named closings when `ChatModelUpdateJudge` is enabled. |
| Closing judge returns a valid negative verdict | The old memory remains. This is not an outage to retry through a more permissive judge. |
| Writer fails | Extractors handle the turn only where `FallBackToExtractorsWhenWriterFails` is enabled. They also require inference and can fail. |
| All required extraction inference fails | A failed attempt is reported; fallback is not proof of a successful write. |

`IMemoryTiers` describes configured tiers. Use the gate trace and extraction outcomes to establish
what actually happened on a request. Missing credentials are checked before the HTTP call; malformed
probability responses are failures, not negative votes.

## Host-specific choices

- **ConversationalAgent sample:** Recommended by default, with its documented legacy switch and
  post-preset overrides. The Agent Framework adapter preserves the core choice.
- **Coding host:** Recommended writing; configured retrieval judges are used unless
  `useRetrievalGate` is false. `closingJudgeName` selects the permitted storage endpoint.
  `allowExtractorFallback` defaults to false and must be enabled explicitly. Inspect each job's
  `extractionPath` separately from its ingestion status. See [coding-service configuration](coding-service.md).
- **General MCP host:** explicit tools remain explicit tools. Configuring the gate changes recall;
  it does not add implicit conversational capture or run every stored tool result through the writer.
- **Custom hosts and Semantic Kernel:** apply the shared registrations you need. Package upgrades
  do not override your composition.

## Imports and compatibility

The writer is for a single user turn with prior conversation supplied as context. Multi-message
imports retain their extraction path and explicit ingestion-mode choices; do not convert an entire
archive into one conversational turn merely to select the writer.

To retain the Conversational configuration, construct that preset rather than partially undoing
Recommended on a reused options object:

```csharp
builder.Services.AddNeo4jAgentMemory(
    MemoryOptions.CreateConversational(),
    neo4j => { /* your connection configuration */ },
    llm => llm.ApplyConversational());
builder.Services.AddAgentMemoryFramework(adapter => adapter.ApplyConversational());
```

Omit gate registration for legacy recall, or explicitly configure `MemoryGateMode.Floor` if your
host already registers it. To disable only the writer, set `UseMemoryWriter = false` after your
chosen preset. These choices affect later requests; they do not undo memories already written.

Existing memories are not automatically re-extracted or deleted. Supersession/invalidation retain
history. Use the normal schema-check/migration workflow for your installed version and extensions;
API graduation itself introduces no graph schema migration. Back up existing data before deploying
an upgrade, and validate your actual providers and host configuration in an isolated environment.

## Experimental module adapters

The Extensibility SDK and validation packs are on their own experimental 0.x release line, published by
`preview-v0.x.y-<prerelease>` tags after the core release they depend on, never at the core's version. Modules
compose statically for the process lifetime (`AddAgentMemoryModules`), judge their items in the core gate's one call
and write through the core writer's closing policy. For that, 2.0 core adds members a host without modules never
touches; recall and context are byte-identical without them:
- `RecallRequest.Companions` and `MemoryContext.CompanionDecisions` (the shared gate pool);
- `MemoryGateCandidate.Companion`: a companion-only type that no judge answers no longer fails the decision;
- `IMemoryClosingApprover` (Abstractions, implemented by Core);
- `PersistedMemoryTurn.CoreWrite`;
- a protected companion overload of `Neo4jMemoryContextProvider.ProvideAIContextAsync`.

Its framework adapter now calls protected `RenderAdmittedBlock` and
`StoredMessageId` methods instead of relying on `InternalsVisibleTo` access to framework internals.
An older adapter binary compiled against those internals is not compatible with the removed friend
grant. Use a matching rebuilt SDK adapter; do not assume an old preview supports the 2.0 framework
package. This does not affect ordinary hosts that use only `Neo4jMemoryContextProvider`.

`StoredMessageId` maps an existing provider ID without modifying the message. Before using that ID
as provenance, verify the source exists in the resolved owner's session; lookup is not a storage
receipt. The new methods add no module activation, persistence or inference calls by themselves.
