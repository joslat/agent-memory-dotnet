# 8.3b: costed, and why a cheap version is not allowed

**Status:** not run. **Date:** 2026-08-13. Costed by preflight — zero provider calls, zero graph writes.

## The measured cost

A **4-question** episodic build (`--memory-types episodic --assistant-content utterance`):

```
frozen preparation preflight 47 calls for 182 source sessions
and 2,123,846 estimated input tokens
```

Scaling to the ~30 episodic questions the decision realistically needs: **~350 calls and ~16M input
tokens per build**. The task requires **three builds per arm across two arms**, so ≈ **96M input
tokens** in total.

That replaces the estimate I had been quoting — "roughly 25× everything else" — with a figure
derived from the harness rather than from my arithmetic.

## Why a small run is not partial progress

It is forbidden by the task's own pre-registered decision rule:

> Ship a non-`Ignore` default only if the episodic mean gain exceeds **that type's own measured noise
> band across ≥3 builds per arm**; otherwise leave it opt-in and publish the null result.

At four questions, accuracy is quantised into 25% steps. The noise band across three builds would
swamp any plausible gain, and the rule would return *"leave it opt-in"* — a **rule-compliant verdict
that is actually about sample size**.

That is precisely the trap 7.6 spent four runs demonstrating: a technically valid negative that
describes the instrument rather than the feature. Producing one here would be worse, because the
pre-registered rule would lend it an authority it had not earned. A decision rule is only worth
having if it is not run at an n it cannot speak to.

## What is needed

Budget for ~96M input tokens, or an explicit decision to lower the question count and **restate the
rule** for the smaller n — which means re-deriving the noise band honestly, not reusing a threshold
designed for a larger sample.

Everything else is ready: `--memory-types episodic` sampling is wired and preflights clean, 8.3a is
merged, and 8.1 (source-role trust) shipped first as the task requires — without it, switching the
mode on would write model-generated claims labelled as the user's.
