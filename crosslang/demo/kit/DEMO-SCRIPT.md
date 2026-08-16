# The 10-minute demo — run sheet

> **Every claim below is footnoted `[S]` shipped, `[P]` prototype, or `[D]` design.** The dotted-muscle
> rule holds in the room: an in-build feature is shown as a design, never demoed as a product. If a
> line has no marker, it is not to be said.

**Total: 10:00.** The section markers are speaking budgets, not measurements. What the dry run
*did* measure is machine time — the scripted run takes 14.4s and the notebook 9.6s (`DRY-RUN.md`);
the rest of the ten minutes is you talking.

---

## Before anyone walks in (T-15 min)

```bash
docker start spike0-neo4j || docker run -d --name spike0-neo4j -p 7688:7687 \
  -e NEO4J_AUTH=neo4j/spikepassword neo4j:5.26

NEO4J_URI=bolt://localhost:7688 NEO4J_USERNAME=neo4j NEO4J_PASSWORD=spikepassword \
ASPNETCORE_URLS=http://localhost:5173 \
dotnet run --project crosslang/spike0/Spike0.Host -c Release

python crosslang/demo/kit/preflight.py     # must print READY
```

Preflight is not optional. It is the difference between finding a dead container now and finding it
in front of the room. **If it does not print `READY`, go to Fallback A and do not improvise.**

Have open, in this order, so no window is ever hunted for:

1. Terminal 1 — the host, already running (never shown; it is just there)
2. Terminal 2 — where `demo_langgraph.py` runs
3. Jupyter — the notebook, kernel started, **cell 1 already executed**
4. Terminal 3 — `python crosslang/demo/kit/screencast.py`, **typed but not run** (this is the
   Fallback-A replay; it needs no host, no container, no network. There is no video: `RECORDING.md`
   says so plainly, and a `.txt` opened in a browser is static text, not the rehearsed fallback)
5. The one-pager, printed, face down

---

## 0:00 – 1:00 · The frame

> "Memory semantics are the part of an agent stack that must not drift. Supersession rules, temporal
> clocks, isolation boundaries, ranking. Every reimplementation of those is a slow fork.
>
> We built them once, in one compiled core, and every language consumes that core through a thin
> client. `[D]` Today I want to show you the part of that which already runs `[S]`, and one thing I
> don't think anyone else can do."

**Do not** open with architecture. The diagram is on the handout; the room's attention is worth more
spent on the terminal.

## 1:00 – 3:00 · Store and resume

Run `python crosslang/demo/kit/../demo_langgraph.py` — beats 1 through 4 scroll past.

> "Writes are **triples**, not documents `[S]`. That matters in about ninety seconds.
>
> When the user comes back, the agent doesn't cold-start. Two reads: a compiled per-owner block —
> point-read, so a global top-K can't starve it `[S]` — and a delta: *what changed since you were last
> here* `[S]`. New facts, and things that were replaced, with both halves so 'updated' reads as an
> update and not as a deletion plus an unrelated creation."

Pause on the `~ was "Acme Corp", now "Initech"` line. That is the whole slide.

## 3:00 – 6:00 · The beat

Switch to the notebook, section 3. **Run the cell live.** Do not scroll to a pre-run output — the room
can tell.

> "Same question, three times. Live: Initech. As of March: Acme. As of September: Initech `[S]`.
>
> The two calls differ by **one dictionary key**. `filter` is LangGraph's own parameter — I did not
> change the `BaseStore` signature, and I couldn't have: `get`, `put` and `search` are LangGraph's own
> concrete methods. Any agent already calling `store.search` gets point-in-time recall by adding one
> key `[P — prototype adapter over a prototype host; the productized SDK follows the published design
> [D]]`."

If someone asks *"couldn't you do that with a timestamp column?"* — that is the best question you will
get. Take it, and go straight to the next section a beat early.

## 6:00 – 8:00 · Why the substitute isn't equivalent

Notebook section 4 — the provenance walk.

> "Here's why that answer is trustworthy rather than merely surprising. The Acme fact is **still
> here** `[S]`. Closed on the transaction clock, not deleted, still carrying its window, still pointing
> at the fact that replaced it.
>
> A store that overwrote has nothing to walk. The March answer isn't slow to find — it stopped
> existing at the moment of the update. That's the difference between a memory system and a key-value
> store with good intentions.
>
> Two clocks, throughout: when it was true, and when we learned it `[S]`."

Then the read-audit line, if the room is technical:

> "The live search shows 1; the historical searches show 0. Auditing the past doesn't move the
> counters that decide how the present ranks `[S]`."

## 8:00 – 9:00 · Isolation, and the honest table

> "Every row carries its owner, so a client can **check** isolation rather than trust it `[S]` —
> enforced centrally, on reads, not just on writes."

Hand out the one-pager. Turn it over to the feature table yourself; do not wait to be asked.

> "The gaps run **both ways**, and this is the table as of the 15th. Your ontology tooling: we don't
> have it. Your GDS algorithms in the adapters: we don't have those either. Your Python and TypeScript
> framework adapters — nine and four — against our zero.
>
> What we have is bitemporal recall across all kinds, read-side isolation, non-destructive
> supersession, decay, a read-audit trail, a schema extension system, and published accuracy
> benchmarks `[S]`.
>
> Read honestly, that table is the argument **for** one core. Nobody has the full set today, and every
> capability on it exists in exactly one of four codebases."

## 9:00 – 10:00 · The ask

> "One engine, thin clients, one conformance kit refereeing that .NET, Python and TypeScript give
> byte-comparably the same answers `[D — the TCK exists and runs 178/178 against .NET today `[S]`;
> the cross-language arms are the design]`.
>
> What I'd like from you is a reaction to the shape, not a commitment. It's an input to what we build
> next — which is precisely why I brought a prototype and not a product."

Stop talking. Ten minutes is ten minutes.

---

## Fallback order — if X breaks, show Y

Rehearse the **transitions**, not just the happy path. Each fallback costs the time in brackets; the
script has ~90 seconds of slack, so exactly one fallback fits without cutting the ask.

| # | If this breaks | Do this | Cost |
|---|---|---|---|
| **A** | Neo4j or the host won't come up (preflight fails) | Run **`python crosslang/demo/kit/screencast.py`** in Terminal 3 — the captured transcript replayed with typing cadence, needing nothing but Python. Say: *"the container's not cooperating — here's the same run from this morning."* Nobody minds; everybody has been there. | 0:30 |
| **B** | Host is up, `demo_langgraph.py` errors mid-run | Skip to the **notebook**, which is an independent client. The beats are the same. | 0:20 |
| **C** | The notebook kernel dies or Jupyter hangs | Re-run `demo_langgraph.py` in Terminal 2 — it covers every beat including provenance, in one shot. | 0:20 |
| **D** | Both clients are dead but the host lives | `curl` the two `as_of` recalls by hand. Raw JSON, two different answers, one changed field. Less pretty, *more* convincing to an engineer. Command is in `preflight.py --curl`. | 0:45 |
| **E** | Everything is dead, no network | The **one-pager** and the screencast, from the laptop, offline. Both are local files. This is why the handout is printed rather than a link. | 0:30 |
| **F** | You are cut to 5 minutes | Beat 3 (`as_of`) and beat 4 (provenance) only. Open with *"one thing, and why it's hard"*, close with the table. Everything else is optional. | — |

**Never** debug live. If something fails twice, take the fallback and keep talking. The room
remembers whether you were in control, not whether the container started.

---

## Claim audit — every spoken claim, and what backs it

Checked in the dry run, line by line. A claim not on this list does not get said.

| Claim in the script | Status | Backing |
|---|:-:|---|
| Bitemporal recall, all kinds, two clocks | `[S]` | `RecallAsOfAsync`; TCK 178/178; live in beats 3–4 |
| Non-destructive supersession | `[S]` | `SUPERSEDED_BY` + transaction-clock closure; visible in the provenance walk |
| Owner isolation enforced on reads | `[S]` | central `IMemoryIsolationPolicy`; `ownerId` on every row |
| Working-memory block, point-read | `[S]` | Wave C 30.4; shown in beat 2 |
| Delta recall, half-open window, checkpoint returned | `[S]` | Wave C; shown in beat 2 |
| Read-audit trail, unmoved by historical reads | `[S]` | `:MemoryReadAudit`; counts visible in beat 4 |
| Schema extension system | `[S]` | 30.14; four shipped extensions, TCK 178/178 with all four on |
| Published accuracy benchmarks | `[S]` | structured 76–90% @ 403 tok/q |
| Decay as re-ranking | `[S]` | recency + structural re-rankers |
| **LangGraph adapter** | `[P]` | prototype over a prototype host, draft wire. **Say "prototype" out loud.** |
| **Python/TS SDKs, embedded NativeAOT, cross-language TCK arms** | `[D]` | designs only. Show the diagram; do not imply code. |
| **Ontology tooling, GDS in adapters, Python/TS framework adapters** | **we don't have these** | conceded in print, on the handout |

The last row is the one that buys the rest of the table its credibility. Do not soften it.
