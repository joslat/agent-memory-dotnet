"""The burst guarantee at its worst admissible case (review 8, G1: the clustered bounds were checked as an average over burst
layouts, while a host's history is one layout, and at some offsets a target at the truth passed 9.4% of the time at 95%).

The regime the guarantee names: bursts of related commits spanning at most BURST (8) eligible commits. Its worst case, for one
layout: bursts of exactly 8, at every offset 0 to 7 from the first drawn task, every task of a burst sharing one outcome
probability (intraclass correlation 1) or close to it (0.6), at every spacing below the burst (1 to 7) and 40 to 200 tasks.
This scans that grid for every clustered bound (hit rate, paired hit difference, one-arm precision, paired precision
difference), then confirms the worst cells with 200,000 simulations each. A bound passes when no confirmed cell exceeds its
nominal rate by more than three Monte Carlo standard errors. Exits 1 when one does.

The bounds are ported to numpy here, and the port is checked against reference.py (pinned to the C# by golden.json) on
sampled inputs before anything is simulated.

usage: python bursts.py [--quick]
"""
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import reference as ref  # noqa: E402
from tq import Z, student_q  # noqa: E402

RNG = np.random.default_rng(20261030)
QUICK = "--quick" in sys.argv


def chunk(X, m):
    n = X.shape[1]
    k = -(-n // m)
    pad = np.zeros((X.shape[0], k * m - n))
    return np.concatenate([X, pad], 1).reshape(X.shape[0], k, m).sum(2)


def lens(n, m):
    k = -(-n // m)
    L = np.full(k, m, float)
    L[-1] = n - m * (k - 1)
    return L


def _lag1_se(values, unseen, w):
    """values (sims, k) in drawn order; the unseen value counted w times. The standard error with the lag-1 term, never lower."""
    k = values.shape[1]
    N = k + w
    mean = (values.sum(1) + w * unseen) / N
    D = values - mean[:, None]
    g0 = ((D ** 2).sum(1) + w * (unseen - mean) ** 2) / (N - 1)
    g1 = (D[:, 1:] * D[:, :-1]).sum(1) / (N - 1)
    return np.sqrt(np.maximum(g0, g0 + 2 * g1) / N)


def clustered_lower(O, C, cap, conf):
    w = ref.HEDGE[conf]
    k = O.shape[1]
    offered = O.sum(1)
    r = C.sum(1) / (offered + w * cap)
    ob = (offered + w * cap) / (k + w)
    se = _lag1_se(C - r[:, None] * O, -r * cap, w)
    q = student_q(Z[conf], k - 1)
    return np.where(offered == 0, 0.0, np.clip(r - q * se / ob, 0, 1))


def clustered_diff(Ao, Ac, Bo, Bc, cap1, cap2, conf):
    w = ref.HEDGE[conf]
    k = Ao.shape[1]
    N = k + w
    o1 = Ao.sum(1) + w * cap1
    c1 = Ac.sum(1)
    o2 = Bo.sum(1) + w * cap2
    c2 = Bc.sum(1) + w * cap2
    r1, r2, m1, m2 = c1 / o1, c2 / o2, o1 / N, o2 / N
    F = (Ac - r1[:, None] * Ao) / m1[:, None] - (Bc - r2[:, None] * Bo) / m2[:, None]
    u = (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2
    q = student_q(Z[conf], k - 1)
    return np.clip(r1 - r2 - q * _lag1_se(F, u, w), -1, 1)


def hit_rate(H, spacing, conf):
    m = ref.cluster_size(spacing)
    L = np.broadcast_to(lens(H.shape[1], m), (H.shape[0], -(-H.shape[1] // m)))
    return clustered_lower(L, chunk(H.astype(float), m), m, conf)


def hit_diff(H1, H2, spacing, conf):
    m = ref.cluster_size(spacing)
    L = np.broadcast_to(lens(H1.shape[1], m), (H1.shape[0], -(-H1.shape[1] // m)))
    return clustered_diff(L, chunk(H1.astype(float), m), L, chunk(H2.astype(float), m), m, m, conf)


def precision(Off, Cor, cap, spacing, conf):
    m = ref.cluster_size(spacing)
    return clustered_lower(chunk(Off.astype(float), m), chunk(Cor.astype(float), m), m * cap, conf)


def precision_diff(O1, C1, O2, C2, cap1, cap2, spacing, conf):
    m = ref.cluster_size(spacing)
    return clustered_diff(chunk(O1.astype(float), m), chunk(C1.astype(float), m), chunk(O2.astype(float), m), chunk(C2.astype(float), m),
                          m * cap1, m * cap2, conf)


def check_port():
    for spacing in (1, 2, 3, 4, 7):
        for n in (10, 37, 40, 101):
            H1 = (RNG.random((40, n)) < 0.4).astype(int)
            H2 = (RNG.random((40, n)) < 0.55).astype(int)
            O1 = RNG.integers(0, 4, (40, n))
            C1 = np.minimum(O1, RNG.integers(0, 4, (40, n)))
            O2 = RNG.integers(0, 4, (40, n))
            C2 = np.minimum(O2, RNG.integers(0, 4, (40, n)))
            for conf in (0.95, 0.99):
                a, b = hit_rate(H1, spacing, conf), hit_diff(H1, H2, spacing, conf)
                c, d = precision(O1, C1, 3, spacing, conf), precision_diff(O1, C1, O2, C2, 3, 3, spacing, conf)
                for i in range(40):
                    t1, t2 = [(1, int(h)) for h in H1[i]], [(1, int(h)) for h in H2[i]]
                    p1, p2 = list(zip(O1[i].tolist(), C1[i].tolist())), list(zip(O2[i].tolist(), C2[i].tolist()))
                    assert abs(a[i] - ref.hit_rate_lower_arm(t1, spacing, conf)) < 1e-9, ("hit rate", spacing, n)
                    assert abs(b[i] - ref.hit_diff_lower_arm(t1, t2, spacing, conf)) < 1e-9, ("hit difference", spacing, n)
                    assert abs(c[i] - ref.precision_lower_arm(p1, 3, spacing, conf)) < 1e-9, ("precision", spacing, n)
                    assert abs(d[i] - ref.precision_diff_lower_arm(p1, 3, p2, 3, spacing, conf)) < 1e-9, ("precision difference", spacing, n)


def burst_probabilities(sims, bursts, p, icc):
    if icc >= 1:
        return (RNG.random((sims, bursts)) < p).astype(float)
    return RNG.beta(p * (1 - icc) / icc, (1 - p) * (1 - icc) / icc, (sims, bursts))


def layout(n, spacing, offset):
    return (np.arange(n) * spacing + offset) // ref.BURST


def pass_rate(kind, n, spacing, offset, icc, p, conf, sims):
    idx = layout(n, spacing, offset)
    bursts = idx.max() + 1
    passes, done = 0, 0
    while done < sims:
        s = min(20000, sims - done)
        if kind == "hit rate":
            H = (RNG.random((s, n)) < burst_probabilities(s, bursts, p, icc)[:, idx]).astype(int)
            passes += int((hit_rate(H, spacing, conf) >= p - 1e-12).sum())
        elif kind == "hit difference":
            # The upgrade and the lift at the truth: two arms that agree burst by burst except where one alone gets it.
            Q = burst_probabilities(s, bursts, p, icc)
            first, second = Q.copy(), Q.copy()
            only = RNG.random((s, bursts))
            first[only < 0.1], second[(only >= 0.1) & (only < 0.2)] = 1.0, 1.0
            H1 = (RNG.random((s, n)) < first[:, idx]).astype(int)
            H2 = (RNG.random((s, n)) < second[:, idx]).astype(int)
            passes += int((hit_diff(H1, H2, spacing, conf) >= 0 - 1e-12).sum())
        elif kind == "precision difference":
            # Two arms of equal precision at the truth, three items a task each, a burst's items all right or all wrong, the arms
            # agreeing burst by burst except where one alone gets it (review 9, B-M2: the bound behind precision lift and the
            # upgrade chain's no-regression on precision was never checked here).
            Q = burst_probabilities(s, bursts, p, icc)
            first, second = Q.copy(), Q.copy()
            only = RNG.random((s, bursts))
            first[only < 0.1], second[(only >= 0.1) & (only < 0.2)] = 1.0, 1.0
            r1 = (RNG.random((s, n)) < first[:, idx]).astype(int)
            r2 = (RNG.random((s, n)) < second[:, idx]).astype(int)
            O = np.full((s, n), 3)
            passes += int((precision_diff(O, 3 * r1, O, 3 * r2, 3, 3, spacing, conf) >= 0 - 1e-12).sum())
        else:
            # One-arm precision: three items a task, a burst's items all right or all wrong.
            Q = burst_probabilities(s, bursts, p, icc)
            right = (RNG.random((s, n)) < Q[:, idx]).astype(int)
            O = np.full((s, n), 3)
            passes += int((precision(O, 3 * right, 3, spacing, conf) >= p - 1e-12).sum())
        done += s
    return passes / sims


# Every clustered bound the gate uses, each at its truths: the count the test expects comes from this list (review 9, B-M2).
KINDS = (("hit rate", (0.2, 0.35, 0.5, 0.8)), ("hit difference", (0.35, 0.6)), ("one-arm precision", (0.3, 0.5)),
         ("precision difference", (0.35, 0.6)))


def main():
    check_port()
    print("the numpy port agrees with reference.py on sampled inputs", flush=True)
    failed = 0
    ns = (40, 200) if QUICK else (40, 100, 200)
    for kind, truths in KINDS:
        for conf in (0.95, 0.99):
            scan = []
            for n in ns:
                for spacing in (1, 2, 3, 4, 5, 6, 7):
                    for offset in range(ref.BURST):
                        for icc in (1.0, 0.6):
                            for p in truths:
                                scan.append((pass_rate(kind, n, spacing, offset, icc, p, conf, 4000 if QUICK else 10000), n, spacing, offset, icc, p))
            scan.sort(reverse=True)
            sims = 50000 if QUICK else 200000
            ceiling = (1 - conf) + 3 * math.sqrt(conf * (1 - conf) / sims)
            worst = max((pass_rate(kind, n, s, o, icc, p, conf, sims), n, s, o, icc, p) for _, n, s, o, icc, p in scan[:4])
            ok = worst[0] <= ceiling
            failed += not ok
            print(f"{'ok  ' if ok else 'OVER'} {kind} at {conf}: worst {worst[0]:.4f} (n {worst[1]}, spacing {worst[2]}, offset {worst[3]}, icc {worst[4]}, "
                  f"truth {worst[5]}), ceiling {ceiling:.4f}", flush=True)
    total = 2 * len(KINDS)
    print(f"{total - failed}/{total} clustered bounds hold their nominal rate at the burst regime's worst case")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
