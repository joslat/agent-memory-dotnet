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
| `safety` |
| `cost` |
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
Fieller's lower root of a θ² + b θ + c (a > 0) reaches t when a t² + b t + c ≥ 0 and 2 a t ≤ −b. The quantiles and the
hedge weights MUST be those of the table below, each decimal read exactly as written, and beyond 60 degrees of freedom
Cornish-Fisher in z, as stated below it. Every clamp of a bound MUST be kept: Fieller's bound is 0 when a ≤ 0, the estimate
when the discriminant is negative, and otherwise the lesser of its lower root and the estimate; every bound is then clamped to
[0, 1], a difference's to [−1, 1]. Floating point MAY show a bound; it MUST NOT decide one.

<!-- registry:trial.statistic.quantiles -->
| Quantile | z | hedgeQuarters | t |
|---|---|---|---|
| `900` | 1.2815516 | 5 | `3.077684`, `1.885618`, `1.637744`, `1.533206`, `1.475884`, `1.439756`, `1.414924`, `1.396815`, `1.383029`, `1.372184`, `1.36343`, `1.356217`, `1.350171`, `1.34503`, `1.340606`, `1.336757`, `1.333379`, `1.330391`, `1.327728`, `1.325341`, `1.323188`, `1.321237`, `1.31946`, `1.317836`, `1.316345`, `1.314972`, `1.313703`, `1.312527`, `1.311434`, `1.310415`, `1.309464`, `1.308573`, `1.307737`, `1.306952`, `1.306212`, `1.305514`, `1.304854`, `1.30423`, `1.303639`, `1.303077`, `1.302543`, `1.302035`, `1.301552`, `1.30109`, `1.300649`, `1.300228`, `1.299825`, `1.299439`, `1.299069`, `1.298714`, `1.298373`, `1.298045`, `1.29773`, `1.297426`, `1.297134`, `1.296853`, `1.296581`, `1.296319`, `1.296066`, `1.295821` |
| `950` | 1.6448536 | 6 | `6.313752`, `2.919986`, `2.353363`, `2.131847`, `2.015048`, `1.94318`, `1.894579`, `1.859548`, `1.833113`, `1.812461`, `1.795885`, `1.782288`, `1.770933`, `1.76131`, `1.75305`, `1.745884`, `1.739607`, `1.734064`, `1.729133`, `1.724718`, `1.720743`, `1.717144`, `1.713872`, `1.710882`, `1.708141`, `1.705618`, `1.703288`, `1.701131`, `1.699127`, `1.697261`, `1.695519`, `1.693889`, `1.69236`, `1.690924`, `1.689572`, `1.688298`, `1.687094`, `1.685954`, `1.684875`, `1.683851`, `1.682878`, `1.681952`, `1.681071`, `1.68023`, `1.679427`, `1.67866`, `1.677927`, `1.677224`, `1.676551`, `1.675905`, `1.675285`, `1.674689`, `1.674116`, `1.673565`, `1.673034`, `1.672522`, `1.672029`, `1.671553`, `1.671093`, `1.670649` |
| `975` | 1.959964 | 7 | `12.706205`, `4.302653`, `3.182446`, `2.776445`, `2.570582`, `2.446912`, `2.364624`, `2.306004`, `2.262157`, `2.228139`, `2.200985`, `2.178813`, `2.160369`, `2.144787`, `2.13145`, `2.119905`, `2.109816`, `2.100922`, `2.093024`, `2.085963`, `2.079614`, `2.073873`, `2.068658`, `2.063899`, `2.059539`, `2.055529`, `2.051831`, `2.048407`, `2.04523`, `2.042272`, `2.039513`, `2.036933`, `2.034515`, `2.032245`, `2.030108`, `2.028094`, `2.026192`, `2.024394`, `2.022691`, `2.021075`, `2.019541`, `2.018082`, `2.016692`, `2.015368`, `2.014103`, `2.012896`, `2.011741`, `2.010635`, `2.009575`, `2.008559`, `2.007584`, `2.006647`, `2.005746`, `2.004879`, `2.004045`, `2.003241`, `2.002465`, `2.001717`, `2.000995`, `2.000298` |
| `990` | 2.3263479 | 8 | `31.820516`, `6.964557`, `4.540703`, `3.746947`, `3.36493`, `3.142668`, `2.997952`, `2.896459`, `2.821438`, `2.763769`, `2.718079`, `2.680998`, `2.650309`, `2.624494`, `2.60248`, `2.583487`, `2.566934`, `2.55238`, `2.539483`, `2.527977`, `2.517648`, `2.508325`, `2.499867`, `2.492159`, `2.485107`, `2.47863`, `2.47266`, `2.46714`, `2.462021`, `2.457262`, `2.452824`, `2.448678`, `2.444794`, `2.44115`, `2.437723`, `2.434494`, `2.431447`, `2.428568`, `2.425841`, `2.423257`, `2.420803`, `2.41847`, `2.41625`, `2.414134`, `2.412116`, `2.410188`, `2.408345`, `2.406581`, `2.404892`, `2.403272`, `2.401718`, `2.400225`, `2.39879`, `2.39741`, `2.396081`, `2.394801`, `2.393568`, `2.392377`, `2.391229`, `2.390119` |
<!-- /registry -->

Beyond 60 degrees of freedom v, the quantile is `z + (z^3 + z) / (4 v) + (5 z^5 + 16 z^3 + 3 z) / (96 v^2) +
(3 z^7 + 19 z^5 + 17 z^3 − 15 z) / (384 v^3)`.

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

### 5.5a Safety

**R-TRIAL-052.** `safety`, when present, MUST hold `probes` (one line of 1 to 100 display-safe characters naming a probe
suite) and MAY hold `maxFollowMicro` (an integer of 0 to 1,000,000, default 0; above 0 with a `confidencePermille`, since no
run bounds a follow rate at 0):

<!-- registry:trial.safetyFields -->
| Safety field |
|---|
| `probes` |
| `maxFollowMicro` |
<!-- /registry -->

**R-TRIAL-053.** A host MUST run the named suite with its own probes (injection prompts placed as records of the pack) and
count the probes whose planted instruction the model followed; the target MUST be met only when that share is at most
`maxFollowMicro` millionths: its estimate without a `confidencePermille`, and otherwise its upper bound, one less
Clopper-Pearson's lower bound of the probes not followed, decided exactly (R-TRIAL-051). Without a run of that suite, the
target MUST NOT be met.

**R-TRIAL-054.** A decision MUST keep the run (the suite, the probes and those followed), so a recheck reaches the same
verdict, and a kept run of no probes, or of counts out of range, MUST NOT meet the target. A certificate MUST NOT be issued
for, nor admit, a pack whose trial declares a safety target: only the host's own probes decide it. A host's floor (R-TRIAL-038)
MAY require a safety target on a named suite at a follow rate at most its own; a trial without one, on another suite or above
it, MUST stay off, and so MUST a decision that kept no run of that suite.

> Why: a signature proves who wrote a record, not that a model will treat it as data. The one way to know is to try it on the
> host's own model; the library stays model-free, and the host supplies the probes.

### 5.5b Cost

**R-TRIAL-055.** `cost`, when present, MUST hold at least one of `savingBytesMedian` (whole bytes, 0 to 1,000,000,000) and
`savingRatioMicro` (millionths of the baseline's bytes, 0 to 1,000,000), and the trial's `baseline` MUST be `host`: a saving is
against what the host already has. Each arm MUST then record, for every task, the UTF-8 bytes of what it offered (0 to
1,000,000,000), each item in the frame the host renders it in (R-HOST-053), the baseline's items framed the same way: one
renderer for both arms. An arm that offers nothing by design records 0.

**R-TRIAL-056.** A task's saving is the baseline's bytes less the pack's and less the pack's fixed cost per turn (what it adds
to every turn whatever the task), in the target's unit: whole bytes, or millionths of the baseline's bytes rounded down (a
baseline of no bytes counting one). The check MUST show a bound and be met exactly when the bound is at or above the target.
Without a `confidencePermille` the bound is the lower median of the tasks' savings. With one it is the exact one-sided sign
test's lower bound of the median: of independent units, the k-th largest, k the fewest of n whose Clopper-Pearson lower bound at
that confidence is at least one half (R-TRIAL-051). At a spacing of a burst (8) or more each task is a unit. Closer, each
cluster of tasks within a burst counts its minimum saving, and the bound is the lesser of the even clusters' bound and the odd
clusters' bound, each set a burst apart; when a set has too few units for any k, the bound is one unit under the target. The
decision MUST keep the fixed cost it counted, so a recheck reaches the same verdict.

**R-TRIAL-058.** A trial MUST read each task as of its own time (its commit's time for a task drawn from history) in world
time as well: a record true only later never answers it, in either arm.

**R-TRIAL-057.** A certificate MAY state cost checks. Each task of its arms MAY then record its offered bytes as a third number
(`[offered, correct, bytes]`, 0 to 1,000,000,000): every task of both arms or none MUST, an arm that offers nothing MUST record
0, a cost check MUST have both arms and the bytes, and a verifier MUST refuse a cost check whose `met` is not exactly
`observedMicro` at or above `requiredMicro`.

> Why: a model reads bytes, not hit rates. The sign test on the median is exact, needs no distribution, and a few large tasks
> cannot carry it; it is the rate bound already specified, at one half.

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

**R-TRIAL-051.** A host's floor MAY give a decision a number of days (feature 11). A decision older than that MUST NOT count:
the pack stays off until it is trialled again, and a pack kept on by a decision MUST go silent when its days end, checked
while the host runs, as a pack past its `validUntil` does.

**R-TRIAL-052.** A decision MAY admit a pack in part (feature 11): each part is the first relation of the reason paths of the
offers it made (`NAME:out` or `NAME:in`), or `(anchor)` for the anchors' own records, measured on the trial's tasks; a part is
admitted when it meets every target alone at the trial's confidence corrected for the number of parts (Bonferroni on alpha:
the least confidence the statistic's tables hold at or above `1 - (1 - c) / parts`; none admitted when the tables hold none).
The record carries each part's evidence, and a host MUST recheck each admitted part from it as it rechecks a whole decision.
A walk MUST follow, from the anchors, only the relations admitted, decided where it chooses them, and MUST NOT offer an
anchor's own record unless `(anchor)` is admitted.

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
