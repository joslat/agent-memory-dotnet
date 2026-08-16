# The demo kit (D3)

> **Prototype.** Everything here drives a throwaway spike host over a draft wire. The productized SDK
> follows the published designs. Nothing is published, packaged, or announced.

Four artifacts, one job: make a 10-minute meeting go well even when something breaks.

| File | What it is | Verified |
|---|---|---|
| [`DEMO-SCRIPT.md`](DEMO-SCRIPT.md) | The run sheet: minute-by-minute, **fallback order**, and a claim audit marking every spoken line `[S]`hipped / `[P]`rototype / `[D]`esign | timings from [`DRY-RUN.md`](DRY-RUN.md) |
| [`agentmemory_langgraph.ipynb`](agentmemory_langgraph.ipynb) | The notebook: store → resume-with-delta → `as_of` → provenance walk | executed end to end, all asserts pass |
| [`ONE-PAGER.md`](ONE-PAGER.md) | The printed handout, including the **honest feature table** with the gaps that run our way | from `one-core-analysis.md` §1 + §4 + §5 |
| [`screencast.txt`](screencast.txt) + [`screencast.py`](screencast.py) | The catastrophic fallback: a real captured transcript, replayed with typing cadence, needing **nothing** to run | replays clean; video still needs a human — see [`RECORDING.md`](RECORDING.md) |

Plus [`preflight.py`](preflight.py), which the run sheet makes mandatory at T-15, and
[`test_store_contract.py`](../test_store_contract.py) — the three defects a closing review found in
the adapter, each red-probed, run first in the rehearsal.

## The order things run

```
preflight.py            → READY, or take Fallback A and stop deciding
demo_langgraph.py       → beats 1–4, one shot, the warm-up
notebook section 3      → THE BEAT, run live in front of the room
notebook section 4      → provenance: why the beat is trustworthy
ONE-PAGER.md            → handed over, table side up
```

## Regenerating

The notebook is **generated**, not hand-edited — a `.ipynb` is JSON with embedded outputs, and
hand-editing one is how a demo ends up with printed output that no longer matches the code above it.

```bash
python build_notebook.py                 # regenerate from build_notebook.py's CELLS
python run_notebook.py                   # execute it against a live host; fails on the first error
python screencast.py --record            # re-capture the fallback transcript
```

Re-record the screencast after any change to the demo. A stale fallback is worse than none: it is
reached when nothing else works, so nobody is in a position to notice it disagrees.

## Two things the kit deliberately does not do

**It does not hide the prototype label.** `/v1/meta` says `PROTOTYPE`, the preflight prints it, the
notebook opens with it, and the run sheet requires saying the word out loud on the adapter claim. In
that room, a claim that outruns the artifact costs more than any missing feature.

**It does not pass on nothing.** Every check that could be satisfied by an empty result — the `as_of`
arms, the delta, the working-memory block — is asserted non-empty, and the two `as_of` answers are
asserted *different*. Two identical answers byte-match perfectly and demonstrate nothing, which is
exactly what a broken `as_of` looks like. That witness exists because the first parity run of this
whole track passed five fixtures while comparing nothing at all.
