"""Exact coverage of the paired precision-difference lower bound (PackTrialGate.PrecisionDifferenceLower, independent tasks,
caps > 0), by enumerating every multinomial outcome of n tasks over a small set of joint task types ((a_offered, a_correct),
(b_offered, b_correct)). The definition is eng/trial-statistics/reference.py (pinned to the C# by golden.json); a vectorized
port is checked against it. A target exactly at the truth should pass at most 1 - conf of the time.

usage: python exact_pdiff.py <repo> <n> <conf> <cap1> <cap2> <random subsets> <seed>
"""
import itertools
import math
import sys

import numpy as np

REPO = sys.argv[1]
sys.path.insert(0, REPO + "/eng/trial-statistics")
import reference as ref  # noqa: E402
from tq import Z, student_q  # noqa: E402

n, conf, cap1, cap2 = int(sys.argv[2]), float(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
S = int(sys.argv[6]) if len(sys.argv) > 6 else 300
seed = int(sys.argv[7]) if len(sys.argv) > 7 else 1
w = ref.HEDGE[conf]
q = student_q(Z[conf], n - 1)

single1 = [(o, c) for o in range(cap1 + 1) for c in range(o + 1)]
single2 = [(o, c) for o in range(cap2 + 1) for c in range(o + 1)]
joint = [(a, b) for a in single1 for b in single2]


def compositions(n, k):
    rows = []
    for bars in itertools.combinations(range(n + k - 1), k - 1):
        prev, row = -1, []
        for b in bars:
            row.append(b - prev - 1)
            prev = b
        row.append(n + k - 1 - prev - 1)
        rows.append(row)
    return np.array(rows, dtype=np.int16)


def bounds(types, K):
    Kf = K.astype(float)
    ao = np.array([t[0][0] for t in types], float); ac = np.array([t[0][1] for t in types], float)
    bo = np.array([t[1][0] for t in types], float); bc = np.array([t[1][1] for t in types], float)
    o1 = Kf @ ao + w * cap1; c1 = Kf @ ac
    o2 = Kf @ bo + w * cap2; c2 = Kf @ bc + w * cap2
    N = n + w
    r1, r2 = c1 / o1, c2 / o2
    m1, m2 = o1 / N, o2 / N
    # f_i = ac/m1 - r1 ao/m1 - bc/m2 + r2 bo/m2, per type, per outcome
    F = (ac[None, :] - r1[:, None] * ao[None, :]) / m1[:, None] - (bc[None, :] - r2[:, None] * bo[None, :]) / m2[:, None]
    sf = (Kf * F).sum(axis=1)
    sff = (Kf * F * F).sum(axis=1)
    u = (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2
    mean = (sf + w * u) / N
    var = (sff - 2 * mean * sf + n * mean * mean + w * (u - mean) ** 2) / (N - 1)
    se = np.sqrt(np.maximum(var, 0) / N)
    return np.clip(r1 - r2 - q * se, -1, 1)


def check(types, K, B, rng):
    for i in rng.choice(len(K), size=min(60, len(K)), replace=False):
        t1 = [types[j][0] for j in range(len(types)) for _ in range(K[i, j])]
        t2 = [types[j][1] for j in range(len(types)) for _ in range(K[i, j])]
        want = ref.precision_diff_lower(t1, cap1, t2, cap2, conf)
        assert abs(want - B[i]) < 1e-9, (t1, t2, want, B[i])


def pass_rate(types, K, B, logkf, p):
    p = np.asarray(p, float) / np.sum(p)
    ao = np.array([t[0][0] for t in types], float); ac = np.array([t[0][1] for t in types], float)
    bo = np.array([t[1][0] for t in types], float); bc = np.array([t[1][1] for t in types], float)
    if (p * ao).sum() == 0 or (p * bo).sum() == 0:
        return float("nan"), 0.0
    truth = (p * ac).sum() / (p * ao).sum() - (p * bc).sum() / (p * bo).sum()
    lp = np.log(p)
    logprob = math.lgamma(n + 1) - logkf + K.astype(float) @ lp
    prob = np.exp(logprob)
    return float(prob[B >= truth - 1e-12].sum()), truth


rng = np.random.default_rng(seed)
worst = []
cache = {}
kinds = [4, 5] if n <= 40 else [3, 4]
for s in range(S):
    k = int(rng.choice(kinds))
    idx = sorted(rng.choice(len(joint), size=k, replace=False).tolist())
    types = [joint[i] for i in idx]
    if k not in cache:
        K = compositions(n, k)
        cache[k] = (K, np.sum(np.vectorize(math.lgamma)(K + 1.0), axis=1))
    K, logkf = cache[k]
    B = bounds(types, K)
    if s < 5:
        check(types, K, B, rng)
    for _ in range(12):
        p = rng.dirichlet(np.full(k, float(rng.choice([0.2, 0.5, 1.0, 3.0]))))
        p = np.maximum(p, 1e-9)
        rate, truth = pass_rate(types, K, B, logkf, p)
        if rate == rate:
            worst.append((rate, truth, types, np.round(p / p.sum(), 3).tolist()))
worst.sort(key=lambda x: -x[0])
print(f"n={n} conf={conf} caps {cap1}/{cap2}: nominal {1 - conf:.3f}; {len(worst)} regimes")
for rate, truth, types, p in worst[:8]:
    print(f"  pass {rate:.4f} truth {truth:+.3f} types {types} p {p}")
