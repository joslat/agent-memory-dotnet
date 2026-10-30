"""Targeted exact search: two arms that agree on most tasks (copies) and differ on a rare task type; the paired precision
difference bound at its truth. Imports the vectorized port from exact_pdiff.py's logic (re-implemented here, checked)."""
import math, sys, itertools
import numpy as np
REPO = sys.argv[1]
sys.path.insert(0, REPO + "/eng/trial-statistics")
import reference as ref
from tq import Z, student_q
conf = float(sys.argv[2]); cap1 = cap2 = 3
w = ref.HEDGE[conf]

def compositions(n, k):
    rows = []
    for bars in itertools.combinations(range(n + k - 1), k - 1):
        prev, row = -1, []
        for b in bars:
            row.append(b - prev - 1); prev = b
        row.append(n + k - 1 - prev - 1); rows.append(row)
    return np.array(rows, dtype=np.int16)

def bounds(types, K, n):
    q = student_q(Z[conf], n - 1)
    Kf = K.astype(float)
    ao = np.array([t[0][0] for t in types], float); ac = np.array([t[0][1] for t in types], float)
    bo = np.array([t[1][0] for t in types], float); bc = np.array([t[1][1] for t in types], float)
    o1 = Kf @ ao + w * cap1; c1 = Kf @ ac; o2 = Kf @ bo + w * cap2; c2 = Kf @ bc + w * cap2
    N = n + w; r1, r2 = c1 / o1, c2 / o2; m1, m2 = o1 / N, o2 / N
    F = (ac[None, :] - r1[:, None] * ao[None, :]) / m1[:, None] - (bc[None, :] - r2[:, None] * bo[None, :]) / m2[:, None]
    sf = (Kf * F).sum(1); sff = (Kf * F * F).sum(1)
    u = (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2
    mean = (sf + w * u) / N
    var = (sff - 2 * mean * sf + n * mean * mean + w * (u - mean) ** 2) / (N - 1)
    return np.clip(r1 - r2 - q * np.sqrt(np.maximum(var, 0) / N), -1, 1)

copies = [((1,1),(1,1)), ((1,0),(1,0)), ((2,2),(2,2)), ((2,1),(2,1)), ((3,3),(3,3)), ((3,1),(3,1)), ((0,0),(0,0)), ((2,0),(2,0))]
losses = [((3,0),(3,3)), ((3,0),(1,1)), ((3,0),(0,0)), ((1,0),(1,1)), ((2,0),(2,2)), ((3,0),(2,2)), ((3,1),(3,3)), ((0,0),(1,1)), ((0,0),(3,3)), ((1,0),(3,3))]
gains = [((1,1),(1,0)), ((3,3),(3,0)), ((1,1),(3,0)), ((2,2),(2,0)), ((1,1),(0,0)), ((3,3),(0,0)), ((0,0),(3,0)), ((0,0),(1,0))]
results = []
for n in [int(x) for x in sys.argv[3].split(",")]:
    for c1, c2 in itertools.combinations(copies, 2):
        for loss in losses:
            for gain in [None] + gains[:4]:
                types = [c1, c2, loss] + ([gain] if gain else [])
                K = compositions(n, len(types))
                logkf = np.sum(np.vectorize(math.lgamma)(K + 1.0), 1)
                B = bounds(types, K, n)
                # spot check against the reference
                for i in np.random.default_rng(1).choice(len(K), 3, replace=False):
                    t1 = [types[j][0] for j in range(len(types)) for _ in range(K[i, j])]
                    t2 = [types[j][1] for j in range(len(types)) for _ in range(K[i, j])]
                    assert abs(ref.precision_diff_lower(t1, cap1, t2, cap2, conf) - B[i]) < 1e-9
                for eps in [0.5 / n, 1 / n, 2 / n, 3 / n, 5 / n, 0.1, 0.2]:
                    for share in [0.2, 0.5, 0.8]:
                        for g in ([0] if gain is None else [eps / 4, eps / 2, eps]):
                            rest = 1 - eps - g
                            if rest <= 0: continue
                            p = np.array([rest * share, rest * (1 - share), eps] + ([g] if gain else []))
                            ao = np.array([t[0][0] for t in types], float); ac = np.array([t[0][1] for t in types], float)
                            bo = np.array([t[1][0] for t in types], float); bc = np.array([t[1][1] for t in types], float)
                            if (p * ao).sum() == 0 or (p * bo).sum() == 0: continue
                            truth = (p * ac).sum() / (p * ao).sum() - (p * bc).sum() / (p * bo).sum()
                            prob = np.exp(math.lgamma(n + 1) - logkf + K.astype(float) @ np.log(np.maximum(p, 1e-300)))
                            results.append((float(prob[B >= truth - 1e-12].sum()), n, truth, types, np.round(p, 4).tolist()))
results.sort(key=lambda r: -r[0])
print(f"conf {conf}: nominal {1-conf:.3f}; {len(results)} regimes")
for r in results[:10]:
    print(f"  pass {r[0]:.4f} n {r[1]} truth {r[2]:+.4f} types {r[3]} p {r[4]}")
