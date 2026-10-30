"""Serially correlated tasks (review 6, H4): commits come in bursts of related changes, and every bound assumes independent
tasks. A history of eligible tasks in bursts (a burst shares a hit probability drawn around the truth, intraclass correlation
`icc`), from which a trial takes `count` tasks: the most recent ones in a row (the round-5 runner) or every k-th, k the most
that still gives the count (the round-6 runner). The pass rate of the Clopper-Pearson hit-rate bound at a target equal to the
truth, against its nominal level; and the same spaced tasks counted as the gate counts them (reference.hit_rate_lower_arm:
closer than a burst of 8, clustered), with its power 0.15 above the target on independent tasks.

usage: python serial.py
"""
import numpy as np
from reference import rate_lower, hit_rate_lower_arm

RNG = np.random.default_rng(6)


def history(n, burst, icc, p):
    """n eligible tasks in bursts of mean length `burst` (geometric), each burst's hit probability Beta with mean p and the icc."""
    a = p * (1 - icc) / icc
    b = (1 - p) * (1 - icc) / icc
    hits, i = np.empty(n, dtype=bool), 0
    while i < n:
        length = RNG.geometric(1 / burst)
        q = RNG.beta(a, b)
        hits[i:i + length] = RNG.random(min(length, n - i)) < q
        i += length
    return hits


def pass_rate(n, count, burst, icc, p, conf, spaced, sims=6000, clustered=False, gap=0.0):
    bound_at = {}
    passes = 0
    for _ in range(sims):
        h = history(n, burst, icc, p) if icc > 0 else RNG.random(n) < p
        stride = max(1, n // count) if spaced else 1
        taken = h[: stride * count: stride][:count]
        if clustered:
            bound = hit_rate_lower_arm([(1, int(x)) for x in taken], stride, conf)
        else:
            k = int(taken.sum())
            if k not in bound_at:
                bound_at[k] = rate_lower(k, count, conf)
            bound = bound_at[k]
        passes += bound >= p - gap - 1e-12
    return passes / sims


if __name__ == "__main__":
    for conf in (0.95, 0.99):
        for burst, icc in ((5, 0.3), (8, 0.3)):
            # Spacings 7, 8 and 9 too (review 7, M9: the grid jumped from 5 to 10, past the burst length the guarantee turns on).
            for n in (40, 80, 200, 280, 320, 360, 400):
                row = [pass_rate(n, 40, burst, icc, 0.5, conf, spaced) for spaced in (False, True)]
                gate = pass_rate(n, 40, burst, icc, 0.5, conf, True, sims=3000, clustered=True)
                power = pass_rate(n, 40, 1, 0.0, 0.5, conf, True, sims=3000, clustered=True, gap=0.15)
                print(f"confidence {conf}, bursts of {burst} (icc {icc}), {n} eligible tasks for 40: in a row {row[0]:.3f}, "
                      f"every {max(1, n // 40)}th {row[1]:.3f}, as the gate counts them {gate:.3f} (nominal {1 - conf:.3f}; "
                      f"power 0.15 above the target {power:.2f})", flush=True)
