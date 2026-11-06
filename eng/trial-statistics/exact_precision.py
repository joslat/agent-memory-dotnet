"""Exact coverage of the one-arm pooled-precision lower bound (PackTrialGate.PrecisionLower, cap > 1), by enumerating every
multinomial outcome of n tasks over the task types (offered, correct). Uses eng/trial-statistics/reference.py (pinned to the
C# by golden.json) as the definition, and a vectorized port checked against it.

usage: python exact_precision.py <repo> <n> <conf> <cap> [regimes]
"""
import itertools
import math
import sys

import numpy as np

REPO = sys.argv[1]
sys.path.insert(0, REPO + "/eng/trial-statistics")
import reference as ref  # noqa: E402
from tq import Z, student_q  # noqa: E402

n = int(sys.argv[2])
conf = float(sys.argv[3])
cap = int(sys.argv[4])
R = int(sys.argv[5]) if len(sys.argv) > 5 else 4000

types = [(o, c) for o in range(cap + 1) for c in range(o + 1)]
k = len(types)
O = np.array([t[0] for t in types], dtype=float)
C = np.array([t[1] for t in types], dtype=float)


def compositions(n, k):
    # all k-tuples of non-negative ints summing to n (stars and bars)
    out = []
    for bars in itertools.combinations(range(n + k - 1), k - 1):
        prev = -1
        row = []
        for b in bars:
            row.append(b - prev - 1)
            prev = b
        row.append(n + k - 1 - prev - 1)
        out.append(row)
    return np.array(out, dtype=np.int16)


K = compositions(n, k)
print("compositions", K.shape, file=sys.stderr)
Kf = K.astype(float)
so = Kf @ O
sc = Kf @ C
soo2 = Kf @ (O * O)
scc2 = Kf @ (C * C)
soc = Kf @ (O * C)

w = ref.HEDGE[conf]
N = n + w
ob = (so + w * cap) / N
cb = sc / N
r = cb / ob
# sums of squares about the weighted means, unseen task (cap, 0) weight w
SOO = (soo2 - 2 * ob * so + n * ob * ob + w * (cap - ob) ** 2) / (N - 1)
SCC = (scc2 - 2 * cb * sc + n * cb * cb + w * cb ** 2) / (N - 1)
SCO = (soc - ob * sc - cb * so + n * ob * cb + w * (cap - ob) * (0 - cb)) / (N - 1)
q = student_q(Z[conf], n - 1)
a = N * ob * ob - q * q * SOO
b = -2 * N * cb * ob + 2 * q * q * SCO
cc = N * cb * cb - q * q * SCC
disc = b * b - 4 * a * cc
with np.errstate(invalid="ignore", divide="ignore"):
    root = np.sqrt(np.maximum(disc, 0))
    lo = np.where(-b + root > 0, 2 * cc / (-b + root), (-b - root) / (2 * a))
bound = np.where(a <= 0, 0.0, np.where(disc < 0, np.clip(r, 0, 1), np.clip(np.minimum(lo, r), 0, 1)))
bound = np.where(so == 0, 0.0, bound)

# check the port against the reference on a sample of outcomes
rng = np.random.default_rng(7)
for i in rng.choice(len(K), size=min(400, len(K)), replace=False):
    tasks = [types[j] for j in range(k) for _ in range(K[i, j])]
    want = ref.precision_lower(tasks, cap, conf)
    assert abs(want - bound[i]) < 1e-9, (tasks, want, bound[i])
print("port agrees with reference.py on 400 outcomes", file=sys.stderr)

lognfact = math.lgamma(n + 1)
logkfact = np.sum(np.vectorize(math.lgamma)(K + 1.0), axis=1) if len(K) < 3_000_000 else None


def pass_rate(p):
    p = np.asarray(p, dtype=float)
    p = p / p.sum()
    eo = (p * O).sum()
    if eo <= 0:
        return float("nan"), 0
    truth = (p * C).sum() / eo
    with np.errstate(divide="ignore"):
        lp = np.where(p > 0, np.log(np.where(p > 0, p, 1)), -np.inf)
    # outcomes using a type with p = 0 have probability 0
    used = K[:, p == 0].sum(axis=1) == 0 if (p == 0).any() else np.ones(len(K), bool)
    logprob = lognfact - logkfact + (Kf * np.where(np.isfinite(lp), lp, 0)).sum(axis=1)
    prob = np.where(used, np.exp(logprob), 0.0)
    return float(prob[bound >= truth - 1e-12].sum()), truth


def describe(p):
    p = np.asarray(p) / np.sum(p)
    return ", ".join(f"{types[j]}:{p[j]:.3f}" for j in range(k) if p[j] > 1e-4)


best = (0, None, None)
results = []
# structured regimes: sizes with weights, precision per size, all-or-none share
def structured(sizes_w, prec, together):
    p = np.zeros(k)
    for o, wgt in sizes_w.items():
        for c in range(o + 1):
            if o == 0:
                pr = 1.0
            else:
                pr = together * (prec[o] if c == o else (1 - prec[o]) if c == 0 else 0) + \
                     (1 - together) * math.comb(o, c) * prec[o] ** c * (1 - prec[o]) ** (o - c)
            p[types.index((o, c))] += wgt * pr
    return p

grid = []
for together in (0.0, 0.5, 1.0):
    for pr in (0.05, 0.1, 0.2, 0.35, 0.5, 0.65, 0.8, 0.9, 0.95, 0.98):
        for weights in ({1: 1, 2: 1, 3: 1}, {1: 1, 3: 1}, {1: 4, 3: 1}, {1: 1, 3: 4}, {3: 1}, {2: 1}, {0: 1, 3: 1}, {0: 3, 3: 1}, {0: 1, 1: 1, 3: 1}, {1: 9, 3: 1}, {1: 19, 3: 1}):
            if cap < 3:
                weights = {min(o, cap): v for o, v in weights.items()}
            for slope in (0.0, -0.5, 0.5):
                prec = {o: min(0.99, max(0.01, pr + slope * (o - 2) / 2)) for o in range(1, cap + 1)}
                grid.append((f"together {together} prec {pr} slope {slope} sizes {weights}", structured(weights, prec, together)))
for name, p in grid:
    rate, truth = pass_rate(p)
    results.append((rate, truth, name))
rng = np.random.default_rng(11)
for i in range(R):
    alpha = rng.choice([0.1, 0.3, 0.7, 1.5])
    p = rng.dirichlet(np.full(k, alpha))
    rate, truth = pass_rate(p)
    results.append((rate, truth, "random " + describe(p)))
# local search from the 10 worst
results.sort(key=lambda x: -(x[0] if x[0] == x[0] else -1))
print(f"n={n} conf={conf} cap={cap}: nominal {1-conf:.3f}")
for rate, truth, name in results[:12]:
    print(f"  pass {rate:.4f}  truth {truth:.3f}  {name}")
