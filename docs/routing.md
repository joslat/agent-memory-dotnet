# Routing: which memory a question reads

By default, recall reads every kind of memory on every turn, up to fixed caps: facts, entities and their relationships,
preferences, the session's messages. The **memory router** chooses per question instead. It is **off** by default
(`MemoryOptions.Routing.Enabled`) until it has passed an accuracy check against reading everything.

## What it does

- **A statement reads nothing.** A turn with no question mark and no request ("I moved to Madrid", "Actually it was
  Tuesday") is extracted, not answered from memory.
- **A question reads facts**, always, and adds a kind when its words call for it:

| Kind | Read when the question… | Example |
|---|---|---|
| `graph` (entities, relationships, GraphRAG) | names a relation of the speaker, asks "who", names someone | "What does my manager's husband do?" |
| `preferences` | asks about tastes, or asks for a suggestion | "Can you suggest a dinner for me tonight?" |
| `messages` (this session's relevant messages) | refers to an episode of the conversation | "What were we planning with the twins?" |

- **Restrict only.** A kind it does not choose gets cap 0; a chosen kind keeps the cap the host configured. Recent
  messages, reasoning traces and shared knowledge are not routed.
- **Recorded.** `MemoryRoutePlan.Routed` (on every recall's context) lists the kinds read and the rule that chose each;
  the trace carries `memory.route.kinds`.
- **On the question.** `RecallRequest.Question` is the turn being answered; the Agent Framework provider sets it to the
  last user message (`Neo4jMemoryContextProvider.QuestionOf`), because its query joins every user message of a turn.

## Configuring it

```csharp
services.AddNeo4jAgentMemory(o =>
{
    o.Routing.Enabled = true;
    // A rule of your own: a pattern that chooses a kind (a kind of your own is allowed).
    o.Routing.Rules.Add(new MemoryRoutingRule("preferences", "music", @"\b(?:playlist|album|song)s?\b"));
}, neo4j => { /* … */ });
```

`Routing.UseDefaultRules = false` drops the shipped rules. A rule's pattern is a .NET regular expression, matched
case-insensitively with a 100 ms timeout; a rule that times out chooses its kind (reading more is the safe error).

## How it is scored

`agentmemory routing-score --set <routing set> [--policy everything|rules] [--split dev|heldout] [--misses]` scores a
policy on a frozen set of questions labelled with the kinds that hold each answer: answers served, reads per turn, reads
wasted, silence on turns that need nothing. On the held-out split the shipped rules serve 22 of 23 answers while reading
1.58 kinds per turn (reading everything: 5.00), and read nothing on 17 of 17 turns that need nothing. Every validation
pack (`agentmemory evaluate --pack core`) also passes with routing on.

## Modules

A module's own memory is routed by the same engine: `IMemoryRouter.Route(question, additionalRules)` evaluates a
module's rules beside the configured ones, compiled without backtracking (`IMemoryRouter.Check` refuses patterns that
need it). See the Extensibility preview for how a module declares them.

## The retrieval memory router: a judge per memory (`AgentMemory.Gate`, experimental)

The router above chooses which **kinds** a question reads, by rules. The retrieval memory router chooses which
**memories** reach the prompt, by a judge: every kind is searched wide (no similarity floor), and the judges score each
memory found against the turn and the conversation; what reaches `Threshold` (0.23) goes in. Measured on two unseen test
sets, every needed memory reached the prompt on 96.1% and 96.5% of turns, against 67.6% and 76.8% for the similarity
floor, with less than half the memory tokens. It is a separate package, opt-in, and every public type is
`[Experimental("AMGATE001")]` while its contracts settle.

| `Mode` | What fills the prompt |
|---|---|
| `Floor` | Recall as without the gate: the similarity floor and a cap per kind. Also the fallback. |
| `Judge` | Everything found, judged memory by memory. |
| `Everything` | Everything found, no cut: 10–17× the memory tokens of `Judge`. |

```csharp
#pragma warning disable AMGATE001
services.AddAgentMemoryGate(configuration.GetSection(GateServiceCollectionExtensions.SectionName));
#pragma warning restore AMGATE001
```

```json
{ "AgentMemory": { "RetrievalRouter": {
    "Mode": "Judge", "Threshold": 0.23, "Timeout": "00:00:03", "UpdateJudge": false,
    "Judges": [ { "Name": "jev", "Endpoint": "https://api.typesafe.ai/v1/systemone", "KeyVariable": "TYPESAFE_API_KEY", "Weight": 0.8 },
                { "Name": "laya", "Endpoint": "http://127.0.0.1:8765/v1/systemone", "Weight": 0.2 } ] } } }
```

- **Never worse than the floor.** A judge that times out (`Timeout`) or fails, a kind no judge answered, or `Judge` with
  no judge configured: recall uses the floor and logs why. A judge that is down is left out of the blend (the weights
  renormalise over those that answered). One that failed `JudgeFailuresBeforeCooldown` calls in a row (3) is not called
  for `JudgeCooldown` (a minute), then asked once: an answer brings it back. So a local judge that is not running costs
  its connection failure (≈2 s on Windows) three times, not on every turn. While every judge is out, recall takes the
  floor (`floor (fallback)`, the reason naming the judge, the span's fallback `cooldown`) and the update judge closes
  nothing. 0 asks every judge on every call. A recall cut by `Timeout` is not counted: the timeout already caps it.
- **Said every time.** Each live recall's context carries a `MemoryGateTrace` in its metadata (`gate.trace`): the mode,
  what ran (`floor (fallback)` with its reason), and every memory considered with its probability and whether it went
  in. A `memory.gate` span carries the counts and the judges' time (`MemoryGateTelemetry`), never a memory's text. An
  as-of recall is not gated.
- **The update judge** (`UpdateJudge = true`) asks the first judge, on the write path, whether each new fact or
  preference replaces one of the owner's most similar stored ones, and closes it at P ≥ 0.65
  (`ExtractionOptions.UpdateJudgeThreshold`); what it closed is on the write's outcome (`IngestionItemOutcome.Closed`).

## The NAMS backend

A store on Neo4j Agent Memory as a Service recalls through its own service, so the router does not apply to it.
