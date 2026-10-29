"""The trial statistics harness (pack format v1.2 section 7; review 5, root RB): how often a target exactly at the truth passes,
and how often a better pack passes, for the round-4 bounds and for the hedged statistic, over named, random, rare-loss and
near-equality regimes. Seeded; the numbers the format states come from this run.

usage: python compare.py [simulations a cell] [random regimes] [confidences, comma-separated] [task counts, comma-separated]

reference.py is the hedged statistic the library implements (the unit test TrialStatisticsTests holds the C# port to its
golden vectors); round4.py is the statistic it replaces, kept to show what the harness found; tq.py is the library's Student's
t table, generated from it.
"""
import sys
import numpy as np
import reference
import round4
from harness import NAMED, Regime, random_regime, one_arm, diff, summary

S = int(sys.argv[1]) if len(sys.argv) > 1 else 1000
R = int(sys.argv[2]) if len(sys.argv) > 2 else 48
CONFS = [float(x) for x in sys.argv[3].split(",")] if len(sys.argv) > 3 else [0.95, 0.99]
NS = [int(x) for x in sys.argv[4].split(",")] if len(sys.argv) > 4 else [10, 40, 100]


def rare_loss(rng, n, m):
    """A rare task (about c/n of them) of m items, all wrong, among tasks of one or two items at a good precision."""
    eps = float(rng.choice([0.5, 1.0, 2.0, 3.0])) / n
    p = float(rng.uniform(0.3, 0.9))
    return Regime([1, 2, m], [(1 - eps) / 2, (1 - eps) / 2, eps], [p, p, 0.0], float(rng.choice([0.0, 1.0])))


def pairs(o, c):
    return list(zip(o.astype(int).tolist(), c.astype(int).tolist()))


METHODS = {
    "round 4": (lambda m: round4.precision_lower, round4.precdiff_lower),
    "hedged": (lambda m: (lambda o, c, cf: reference.precision_lower(pairs(o, c), m, cf)),
               lambda o1, c1, o2, c2, cf: reference.precision_diff_lower(pairs(o1, c1), 3, pairs(o2, c2), 3, cf)),
}
for conf in CONFS:
    print(f"== confidence {conf} (nominal {1 - conf:.3f}); share of trials passing a target at the truth, {S // 2} a cell")
    for n in NS:
        for label, (one, two) in METHODS.items():
            rng = np.random.default_rng(int(conf * 1000) + n)
            worst = []
            for m in (3, 16):
                worst += [one_arm(one(m), random_regime(rng, m), n, S // 2, rng, conf) for _ in range(R // 2)]
                worst += [one_arm(one(m), rare_loss(rng, n, m), n, S // 2, rng, conf) for _ in range(R // 4)]
                worst += [one_arm(one(m), r, n, S // 2, rng, conf) for r in NAMED.values() if r.sizes.max() <= m]
            dw = []
            for _ in range(R // 2):
                a, b = random_regime(rng, 3), random_regime(rng, 3)
                dw.append(diff(two, a, float(rng.choice([0.0, 0.5, 0.8, 0.95, 0.98])), b, n, S // 2, rng, conf))
            base, broken = NAMED["all-or-none .5, 0-3"], Regime([0, 1, 2, 3], [1, 1, 1, 1], [0, 0, 0, 0], 1.0)
            near = [diff(two, base, ag, broken, n, S // 2, rng, conf) for ag in (0.9, 0.95, 0.98)]
            p1 = [one_arm(one(3), NAMED["iid .45, 1-3"], n, S // 2, rng, conf, gap=g) for g in (0.10, 0.15)]
            lift_a, lift_b = Regime([3], [1], [.19], 0.0), Regime([1, 2, 3], [1, 1, 1], [.45, .45, .45], 0.0)
            p2 = diff(two, lift_a, 0.0, lift_b, n, S // 2, rng, conf, gap=0.21)
            print(f"  n={n:3d} {label:8s} one arm {summary(worst)}")
            print(f"  {'':14s} difference {summary(dw)}; near-equality (agree .9/.95/.98) {' / '.join(f'{x:.3f}' for x in near)}")
            print(f"  {'':14s} power: precision +.10/+.15 {p1[0]:.2f}/{p1[1]:.2f}; a lift of .26 against a target of .05 {p2:.2f}")
