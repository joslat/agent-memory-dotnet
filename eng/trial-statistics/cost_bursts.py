"""The cost check's level at its worst admissible case (feature 05 review, H1: clusters of the median, tested together, passed
5.9% of the time at 95% with tasks one commit apart, because neighbouring clusters share a burst).

The check (exact.cost_bound, PackTrialGate.CostCheck): each cluster counts its minimum saving; with tasks closer than a burst,
the bound must hold on the even clusters and on the odd ones, each set a burst apart and so independent; at a spacing of a
burst or more every task is its own cluster. The regime is bursts.py's: bursts of exactly 8 eligible commits, at every offset 0
to 7, every task of a burst sharing its outcome (intraclass correlation 1) or 0.6, every spacing 1 to 8, 40 to 200 tasks. At
the boundary of the null hypothesis each task saves at least the target with probability one half. A cell passes when its
rate stays within three Monte Carlo standard errors of alpha. Exits 1 when one does not.

The verdict is reduced exactly to counts: the k-th largest unit is at or above the target when at least k units are, k the
fewest that exact.sign_k gives, so the simulation checks the reference's own k, never a copy.

usage: python cost_bursts.py [--quick]
"""
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import exact  # noqa: E402

RNG = np.random.default_rng(20261031)
QUICK = "--quick" in sys.argv
CONFIDENCES = (0.9, 0.95, 0.975, 0.99)


def outcomes(n, spacing, offset, icc, sims):
    """Whether each task saves at least the target: one coin per burst of 8, kept by a task with probability icc."""
    burst = (offset + np.arange(n) * spacing) // exact.BURST
    shared = RNG.random((sims, burst.max() + 1)) < 0.5
    x = shared[:, burst]
    if icc < 1:
        x = np.where(RNG.random((sims, n)) < icc, x, RNG.random((sims, n)) < 0.5)
    return x


def met(x, spacing, conf):
    """exact.cost_bound reaching the target, on outcomes: units are clusters' minima; the halves when clustered."""
    n, m = x.shape[1], exact.cluster_size(spacing)
    units = x if m == 1 else np.stack([x[:, c:c + m].all(1) for c in range(0, n, m)], 1)
    sets = [units] if m == 1 else [units[:, 0::2], units[:, 1::2]]
    out = np.ones(x.shape[0], bool)
    for s in sets:
        k = exact.sign_k(s.shape[1], conf)
        out &= (s.sum(1) >= k) if k is not None else False
    return out


def main():
    sims = 4000 if QUICK else 20000
    worst = []
    for conf in CONFIDENCES:
        a = float(exact.alpha(conf))
        for n in ((40, 100) if QUICK else (40, 60, 100, 200)):
            for spacing in range(1, 9):
                for icc in (1.0, 0.6):
                    for offset in range(exact.BURST):
                        rate = met(outcomes(n, spacing, offset, icc, sims), spacing, conf).mean()
                        worst.append((rate - a, conf, n, spacing, icc, offset, rate))
    worst.sort(reverse=True)
    failed = 0
    confirm = 40000 if QUICK else 200000
    for _, conf, n, spacing, icc, offset, _ in worst[:8]:
        a = float(exact.alpha(conf))
        rate = met(outcomes(n, spacing, offset, icc, confirm), spacing, conf).mean()
        se = math.sqrt(a * (1 - a) / confirm)
        ok = rate <= a + 3 * se
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} conf {conf} n {n} spacing {spacing} icc {icc} offset {offset}: {rate:.4f} against {a} (+3 se {a + 3 * se:.4f})")
    print(f"{len(worst)} cells scanned; the 8 worst confirmed with {confirm} simulations each: {'every one within' if not failed else f'{failed} above'} its level")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
