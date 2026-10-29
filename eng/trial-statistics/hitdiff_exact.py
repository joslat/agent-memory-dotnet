"""Exact coverage of the paired hit-difference bound (review 6, root RB): for every n and every pair of rates (p+, p-) on a
grid, the probability, summed over every outcome (n+, n-) of the trinomial, that the bound reaches the true difference
p+ - p-. No Monte Carlo: this is the worst pass rate at the truth, up to the grid. It chose reference.HEDGE.

usage: python hitdiff_exact.py [weight to try] [confidence]    (no arguments: the reference's weights at every confidence)
"""
import math
import sys
import numpy as np
from reference import HEDGE
from tq import Z, student_q

LF = np.array([math.lgamma(i + 1) for i in range(402)])
NS = (10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 130, 160, 200)


def bounds(n, w, conf):
    """The bound for every outcome (n+, n-) with n+ + n- <= n, as reference.hit_diff_lower computes it."""
    q = student_q(Z[conf], n - 1)
    P, M = np.meshgrid(np.arange(n + 1), np.arange(n + 1), indexing="ij")
    ok = P + M <= n
    N = n + w
    mean = (P - M - w) / N
    ss = P * (1 - mean) ** 2 + M * (-1 - mean) ** 2 + np.maximum(n - P - M, 0) * mean ** 2 + w * (-1 - mean) ** 2
    L = np.clip(mean - q * np.sqrt(ss / (N - 1) / N), -1, 1)
    return P, M, ok, L


def worst(conf, w, grid=30):
    best = (0.0, None)
    for n in NS:
        P, M, ok, L = bounds(n, w, conf)
        Z0 = np.maximum(n - P - M, 0)
        base = LF[n] - LF[P] - LF[M] - LF[Z0]
        rates = np.concatenate([[1e-12], np.geomspace(0.002, 0.5, grid)])
        for pp in rates:
            for pm in rates:
                if pp + pm >= 1:
                    continue
                lp = base + P * math.log(pp) + M * math.log(pm) + Z0 * math.log(1 - pp - pm)
                passed = float(np.exp(lp[ok & (L >= pp - pm - 1e-12)]).sum())
                if passed > best[0]:
                    best = (passed, (n, round(float(pp), 4), round(float(pm), 4)))
    return best


if __name__ == "__main__":
    if len(sys.argv) > 2:
        w, conf = float(sys.argv[1]), float(sys.argv[2])
        print(conf, w, worst(conf, w))
    else:
        for conf, w in HEDGE.items():
            passed, where = worst(conf, w)
            print(f"confidence {conf}, weight {w}: worst pass at the truth {passed:.4f} (nominal {1 - conf:.3f}) at n, p+, p- = {where}")
