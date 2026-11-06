"""The trial statistics harness (root RB): how often a target exactly at the truth passes, per method, over named and random
regimes. A method is honest at level delta when that rate stays at or below delta (Monte Carlo error aside)."""
import math
import sys
import numpy as np


class Regime:
    """A distribution of one task's (offered, correct): sizes with weights, a precision per size, a share of all-or-none tasks."""

    def __init__(self, sizes, weights, prec, together, name=""):
        self.sizes = np.array(sizes)
        self.weights = np.array(weights, dtype=float) / np.sum(weights)
        self.prec = np.array(prec, dtype=float)
        self.together = together
        self.name = name

    def truth(self):
        den = (self.weights * self.sizes).sum()
        return float((self.weights * self.sizes * self.prec).sum() / den) if den > 0 else 0.0

    def hit_rate(self):
        # P(correct > 0): all-or-none p, iid 1 - (1-p)^o
        iid = 1 - (1 - self.prec) ** self.sizes
        hit = self.together * self.prec * (self.sizes > 0) + (1 - self.together) * iid
        return float((self.weights * hit).sum())

    def draw(self, rng, shape):
        k = rng.choice(len(self.sizes), size=shape, p=self.weights)
        o = self.sizes[k]
        p = self.prec[k]
        tog = rng.random(shape) < self.together
        c = np.where(tog, np.where(rng.random(shape) < p, o, 0), rng.binomial(o, p))
        return o, c


def random_regime(rng, m=None):
    m = m or int(rng.choice([3, 3, 16]))
    support = np.unique(rng.choice(np.arange(0, m + 1), size=int(rng.integers(1, min(m + 1, 5) + 1))))
    if (support == 0).all():
        support = np.array([0, 1])
    weights = rng.dirichlet(np.full(len(support), 0.7))
    base = rng.uniform(0.02, 0.98)
    slope = rng.choice([0.0, rng.uniform(-0.8, 0.8)])
    prec = np.clip(base + slope * (support - support.mean()) / max(1, m), 0.01, 0.99)
    together = float(rng.choice([0.0, 1.0, rng.uniform(0, 1)]))
    return Regime(support, weights, prec, together, f"m{m} sizes {support.tolist()} prec {np.round(prec, 2).tolist()} together {together:.2f}")


NAMED = {
    "iid .45, 1-3": Regime([1, 2, 3], [1, 1, 1], [.45, .45, .45], 0.0),
    "all-or-none .9, 1-3": Regime([1, 2, 3], [1, 1, 1], [.9, .9, .9], 1.0),
    "all-or-none .5, 0-3": Regime([0, 1, 2, 3], [1, 1, 1, 1], [.5, .5, .5, .5], 1.0),
    "size-dependent .67/.21 (r5 M5)": Regime([1, 3], [1, 1], [.67, .21], 0.0),
    "size-dependent skewed .67/.21 (r5 M5)": Regime([1, 3], [4, 1], [.67, .21], 0.0),
    "skew 1 or 16, all-or-none .6/.1": Regime([1, 16], [9, 1], [.6, .1], 1.0),
    "few tasks many items (r4)": Regime([1, 16], [19, 1], [.8, .95], 1.0),
    "near zero .03": Regime([1, 2, 3], [1, 1, 1], [.03, .03, .03], 0.0),
}


def one_arm(method, regime, n, sims, rng, conf, gap=0.0):
    truth = regime.truth() - gap
    if truth <= 0:
        return float("nan")
    o, c = regime.draw(rng, (sims, n))
    passes = 0
    for i in range(sims):
        if o[i].sum() == 0:
            continue
        if method(o[i], c[i], conf) >= truth - 1e-12:
            passes += 1
    return passes / sims


def paired(regime2, agree, regime1, rng, shape):
    """Arm 2 from regime2; arm 1 copies it on a share `agree` of tasks, else draws from regime1."""
    o2, c2 = regime2.draw(rng, shape)
    o1n, c1n = regime1.draw(rng, shape)
    copy = rng.random(shape) < agree
    return np.where(copy, o2, o1n), np.where(copy, c2, c1n), o2, c2


def paired_truth(regime2, agree, regime1):
    w2, w1 = regime2.weights, regime1.weights
    e_o2, e_c2 = (w2 * regime2.sizes).sum(), (w2 * regime2.sizes * regime2.prec).sum()
    e_o1n, e_c1n = (w1 * regime1.sizes).sum(), (w1 * regime1.sizes * regime1.prec).sum()
    e_o1, e_c1 = agree * e_o2 + (1 - agree) * e_o1n, agree * e_c2 + (1 - agree) * e_c1n
    return float(e_c1 / e_o1 - e_c2 / e_o2)


def diff(method, regime2, agree, regime1, n, sims, rng, conf, gap=0.0):
    truth = paired_truth(regime2, agree, regime1) - gap
    o1, c1, o2, c2 = paired(regime2, agree, regime1, rng, (sims, n))
    passes = 0
    for i in range(sims):
        if o1[i].sum() == 0 or o2[i].sum() == 0:
            continue
        if method(o1[i], c1[i], o2[i], c2[i], conf) >= truth - 1e-12:
            passes += 1
    return passes / sims


def summary(rates):
    r = np.array([x for x in rates if not math.isnan(x)])
    return f"max {r.max():.3f}  p95 {np.quantile(r, .95):.3f}  median {np.median(r):.3f}  over {len(r)}"
