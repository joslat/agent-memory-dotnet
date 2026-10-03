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

## The NAMS backend

A store on Neo4j Agent Memory as a Service recalls through its own service, so the router does not apply to it.
