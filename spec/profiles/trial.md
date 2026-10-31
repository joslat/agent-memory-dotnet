# Trial profile

**Version 1, draft.** Conformance class: **Trial Host**. Requirement ids: `R-TRIAL-nnn`. Key words as in Core §1.2.
Statistic: `agentmemory.trial.hedged/4`. Pack tests format: `agentmemory.pack.tests/1.2`.

## 1. Introduction

### 1.1 Scope

A pack that declares a trial is switched on in a host only when it passed that trial on the host's own history: the host
draws tasks from its history, measures its retrieval with the pack and without it, and decides by declared targets, on a
confidence bound when the pack asks for one. The decision is a record the host signs (`host/decision-record.md`), bound to
exactly the content it trialled. The profile also defines a pack's own deterministic tests and the rule for upgrades.

### 1.2 When a pack uses it

By the registry's profile map (Core §13), a pack uses the Trial profile when it holds `evals/trial.json` or
`evals/tests.json`.

### 1.3 Conformance

**R-TRIAL-001.** A Trial Host MUST implement Ampack Core as a Core Reader, the Walk profile, and every requirement of
this document.

## 2. evals/trial.json

### 2.1 Fields

<!-- registry:trial.fields -->
| Field |
|---|
| `measure` |
| `tasks` |
| `baseline` |
| `confidencePermille` |
| `targets` |
| `upgrade` |
<!-- /registry -->

| Field | Meaning |
|---|---|
| `measure` | What is measured: `test-impact`; `retrieval` (expected keys for recorded questions) is reserved |
| `tasks` | How many tasks the host draws from its own history |
| `baseline` | What "without the pack" means: `host` (the host's existing retrieval: search, or grep for code) or `none` |
| `targets` | The targets, each of which must be met |
| `confidencePermille` | Optional: the confidence, in thousandths, at which every target is judged by its lower bound |
| `upgrade` | Optional: `{"noRegression": true, "toleranceMicro": 20000}`, the rule for a new version (§8) |

The targets:

<!-- registry:trial.targets -->
| Target | field |
|---|---|
| `hitRate` | hitRateMicro |
| `precision` | precisionMicro |
| `lift` | liftMicro |
| `precisionLift` | precisionLiftMicro |
| `addedP95Ms` | addedP95Ms |
<!-- /registry -->

**R-TRIAL-002.** `measure` MUST be `test-impact`; `retrieval` is reserved, and a host that does not implement it MUST
refuse a trial that declares it.

**R-TRIAL-003.** `tasks` MUST be 10 to 200 (default 40), and at least 10 tasks MUST be found for a trial to decide
anything.

**R-TRIAL-004.** `baseline` MUST be `host` or `none` (default `none`).

**R-TRIAL-005.** `hitRateMicro` and `precisionMicro` MUST be 0 to 1,000,000, `liftMicro` and `precisionLiftMicro`
−1,000,000 to 1,000,000, and `addedP95Ms` 1 to 60,000, each an integer (R-CORE-119); every target given MUST be met for the
trial to pass.

**R-TRIAL-006.** `confidencePermille`, when present, MUST be 900, 950, 975 or 990; a target MUST then be met only by its
lower confidence bound under the statistic of §5.

**R-TRIAL-007.** `upgrade.toleranceMicro` MUST be 0 to 500,000 (default 0); with a `confidencePermille`, a
`noRegression` rule MUST have a tolerance above 0.

**R-TRIAL-008.** A trial with no measure, or with a target out of range, MUST be refused.

## 3. Tasks

**R-TRIAL-009.** A Trial Host MUST draw the tasks from its own history.

**R-TRIAL-010.** Tasks MUST be drawn in history order: the runner draws every k-th eligible commit of the last
2,000, k the most that still gives the count, and each arm MUST record `spacing`, every how many eligible commits a task
was drawn (1 to 2,000).

**R-TRIAL-011.** A decision MUST record how its tasks were drawn, `history`: `parent` (each task at its commit's
parent, the default) or `head` (the repository as it is now, which sees the tests a change created).

**R-TRIAL-012.** Both arms of a trial MUST run the same tasks, drawn at one spacing, within the 2,000 commits a
trial draws from.

> Why: commits come in bursts of related changes, and tasks drawn one after another from a burst are not independent;
> the spacing lets every bound cluster them (§5.4). The stated assumption is that a burst spans at most 8 eligible commits.

## 4. Arms and their evidence

An *arm* is one side of the measurement: `with` the pack, and `without` it under the baseline.

**R-TRIAL-013.** Each arm MUST record `maximumOffered`, fixed before it ran: the pack's `maxItems` for the arm with the
pack, 3 for the grep baseline, 0 for the baseline `none` (which offers nothing by design), and 16 when unstated.

**R-TRIAL-014.** Each arm MUST record its per-task results (`perTask`): for each task, the items offered and the
items right, each task within the arm's cap; the arm's totals MUST be the per-task results' own.

**R-TRIAL-015.** A hit MUST be a task with a right item.

**R-TRIAL-016.** Precision, and every difference, MUST be bounded only from per-task results, on both arms
for a difference; without them, a host MUST NOT take a precision or a difference as bounded.

> Why: the items one task offers are correlated, so precision is bounded with each task as one cluster.

## 5. The statistic

The statistic `agentmemory.trial.hedged/4` turns the arms' evidence into lower bounds, one per target.

**R-TRIAL-051.** Whether a target is met MUST be decided exactly, in rational arithmetic, never in floating point. An
estimate is a ratio of counts. Clopper-Pearson's bound reaches t when P(X ≥ x) at rate t is at most 1 − confidence. A bound
of the form mean − q·se (the hedged, paired and clustered bounds) reaches t when mean − t ≥ 0 and (mean − t)² ≥ q²·se².
Fieller's lower root of a θ² + b θ + c (a > 0) reaches t when a t² + b t + c ≥ 0 and 2 a t ≤ −b. The quantiles are the
decimals of the statistic's table, the hedge weights quarters, and every clamp of a bound is kept. Floating point MAY show
a bound; it MUST NOT decide one.

> Why: two implementations whose logarithms differ in the last bit can reach two verdicts at a boundary. A verdict never
> needs the bound's value, only whether it reaches the target, and that is an inequality of rationals.

### 5.1 Without a confidence

**R-TRIAL-017.** Without a `confidencePermille`, a target MUST be judged on its estimate.

### 5.2 Hit rate

**R-TRIAL-018.** A hit rate's lower bound MUST be Clopper-Pearson's, exact; so MUST precision's be when an arm offers
one item a task at most.

### 5.3 Precision and differences

**R-TRIAL-019.** Precision otherwise, and every difference, MUST assume tasks the trial did not see, at the worst the
arm's cap allows: the unseen task offers the arm's cap, none right, and for a difference the other arm offers its cap,
all right.

**R-TRIAL-020.** The unseen task MUST be counted 1.25, 1.5, 1.75 or 2 times, at the confidences 0.9, 0.95,
0.975 and 0.99 respectively, beside the tasks the trial ran.

**R-TRIAL-021.** The bound MUST then be Fieller's test inversion of the pooled precision, or the difference
linearized task by task, at Student's t with one degree of freedom fewer than the tasks.

**R-TRIAL-022.** Only an arm that offers nothing by design (a cap of 0) MUST be taken to have no uncertainty;
an arm that could offer but did not MUST keep its unseen task.

> Why: the unseen task is the rule of three's lesson: without it, a sample that never met a rare task with a large loss
> passes as if there were none. The weights are the least, in quarters, at which a paired hit difference equal to its
> target passes at most the nominal rate for every n from 10 to 200 and every pair of rates, by exact enumeration of the
> trinomial (the rates reached are 9.4%, 4.6%, 2.1% and 0.85%).

### 5.4 Clustering

**R-TRIAL-023.** When tasks were drawn closer than 8 eligible commits apart, every bound MUST first cluster each run
of ⌈8 / spacing⌉ consecutive tasks, in drawn order, into one (a hit rate then becomes a clustered proportion).

**R-TRIAL-024.** Neighbouring runs MUST count as correlated, since a burst of at most 8 commits can straddle two: every
clustered bound MUST add the runs' lag-1 autocovariance to its variance, never lowering it.

**R-TRIAL-025.** The clustered one-arm bound MUST be the hedged pooled ratio linearized at its estimate.

### 5.5 Latency

**R-TRIAL-026.** `addedP95Ms` MUST be compared as measured, with no bound.

### 5.6 Re-derivation

**R-TRIAL-027.** A host MUST derive every verdict again from the evidence a decision carries, under its own
statistic; the statistic a record names (`statistic`) is for information. Re-deriving MAY take a pass back, and MUST NOT
give one.

**R-TRIAL-028.** A writer's lint SHOULD say what a declared trial can never show, for hit rate and precision both.

> Why: forty tasks cannot certify everything. Two versions that agree on every task bound their difference at −0.086 at
> best (every task offering its whole cap), so a no-regression tolerance under about 0.09 needs more tasks, whatever the
> data. Under bursts of geometric length averaging 5 at an intraclass correlation of 0.3, a hit-rate target at the truth
> passes at most 4.6% at 95% and 1.2% at 99%; the price is power on short histories.

## 6. The decision

**R-TRIAL-029.** A trial MUST pass exactly when every declared target is met on at least 10 tasks, each target judged as
§5 says.

**R-TRIAL-030.** A Trial Host MUST record each decision as a decision record signed by its own key
(`host/decision-record.md`), bound to the pack's digest and the declarations' digest.

**R-TRIAL-031.** A host MUST find a decision by id, version and both digests: a later trial of other content MUST NOT
shadow it, a decision for one pack MUST NOT switch on other records under the same declarations, and a decision for
compiled code MUST NOT count for a pack, nor the reverse.

**R-TRIAL-032.** A decision record that names no pack digest MUST still verify, and MUST count for nothing.

**R-TRIAL-033.** A record that was edited, or signed by a key other than the host's, MUST NOT switch a pack
on.

## 7. Activation

**R-TRIAL-034.** A host that requires trials MUST keep off a pack that declares a trial and has no passing decision for
exactly itself, and whatever needs that pack; a failed trial MUST keep the pack off with its reason.

**R-TRIAL-035.** A pack kept off MUST keep its contributor slots registered, contributing nothing, so every other
module's contributors still reach the host.

**R-TRIAL-036.** A host that does not require trials MUST compose packs as it would without this profile.

**R-TRIAL-037.** A host MAY require trials under id prefixes, by whole segments; a pack there that declares no trial
MUST stay off.

**R-TRIAL-038.** A host MAY set a floor, the least a trial must claim before it counts: a confidence, a task count and
targets at least as strict. A trial below the floor MUST stay off, with what it lacks; the floor MUST be judged on the
tasks a decision ran, not on what its pack declares, and a floor's targets MUST be held to the rules a manifest's are.

**R-TRIAL-039.** Every door that shows or serves what a host composes (a gate report, a server) MUST take the same
gate options, the floor among them, and decide by the same rule as the host.

## 8. Upgrades

**R-TRIAL-040.** With `upgrade.noRegression`, a new version MUST switch on only if it does no worse than the
version that passed on this host before it, on the same tasks.

**R-TRIAL-041.** With a `confidencePermille`, the rule MUST be a non-inferiority test: the lower bound of each drop
clears −tolerance; without one, the estimates MUST NOT drop by more than the tolerance.

**R-TRIAL-042.** The runner MUST measure the version that passed on this host on the same tasks in the same
run, the decision MUST keep that comparison, and two more targets MUST require no drop in hit rate or precision beyond
the tolerance.

**R-TRIAL-043.** At composition, a passing upgrade of a version that passed here MUST stay off unless its
decision compared it with exactly that version (id, version and both digests).

**R-TRIAL-044.** The version an upgrade is compared with MUST be the latest one this host's own trial
passed and that the gate itself allows (its floor included); a decision made from a certificate MUST NOT stand as that
version, and a version whose latest decision failed MUST NOT count.

**R-TRIAL-045.** The version an upgrade replaces MUST be held to the cap this host judged it at, never the cap the
upgrade's decision states.

**R-TRIAL-046.** Every rule down the chain of versions MUST hold, each as declared, its tolerance with its own
confidence; a version MUST NOT drop or loosen the rule its predecessor was held to.

**R-TRIAL-047.** A first version MUST NOT be held to a predecessor; when neither version asks for the rule, an
upgrade MUST compose without comparison; the same version with other content MUST be an upgrade too.

> Why: the stricter of two rules is not a blend of the smallest tolerance and the highest confidence, which is looser than
> either; every rule holds as it was declared. Only a version of the same form is a predecessor today: a pack that
> replaces compiled code of the same id starts a new chain.

## 9. Pack tests

`evals/tests.json` (format `agentmemory.pack.tests/1.2`) holds deterministic unit tests: a view's search or key, or a walk
from anchors with a question; the keys expected and not expected, optionally within the first N.

**R-TRIAL-048.** A host's composer and a writer's test command MUST run a pack's tests offline on the same engine the
walk uses, and a failing test MUST name what is missing.

**R-TRIAL-049.** A malformed tests file MUST be refused, naming the test it concerns.

**R-TRIAL-050.** A writer's lint SHOULD advise a knowledge pack without a trial that a host requiring trials keeps it
off, and SHOULD advise a trial without an upgrade rule or with few tasks.
