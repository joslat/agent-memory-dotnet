"""The exact decision for the trial bounds (feature 02): whether each bound of reference.py reaches a target, decided in
rational arithmetic, so every implementation reaches the same verdict. The C# port is PackTrialExact; golden.json carries a
grid of these decisions, which TrialStatisticsTests holds the port to.

A verdict never needs a bound's value, only whether it reaches the target t:

- Clopper-Pearson: the bound reaches t exactly when P(X >= x) at rate t is at most 1 - confidence, a finite sum of rationals.
- A mean less q times its standard error (the hedged, paired and clustered bounds): it reaches t exactly when mean - t >= 0 and
  (mean - t)^2 >= q^2 se^2, where se^2 is a ratio of counts and q is the table's decimal (or Cornish-Fisher in it).
- Fieller's lower root of a theta^2 + b theta + c (a > 0): it reaches t exactly when a t^2 + b t + c >= 0 and 2 a t <= -b.

The clamps of reference.py are kept: a bound clamped to [0, 1] reaches any t <= 0 and no t > 1. The quantiles are the decimals
of tq.py, read as decimals; the hedge weights are quarters. Floating point may still show a bound to people; a verdict never.
"""
from fractions import Fraction as Q

from tq import TABLE, Z

HEDGE = {0.9: Q(5, 4), 0.95: Q(3, 2), 0.975: Q(7, 4), 0.99: Q(2)}
BURST = 8


def q_of(conf, df):
    """Student's t quantile, exactly as the table's decimals or Cornish-Fisher in the normal quantile's decimal."""
    z = Q(repr(Z[conf]))
    v = max(1, df)
    if v <= 60:
        return Q(repr(TABLE[Z[conf]][v - 1]))
    return z + (z ** 3 + z) / (4 * v) + (5 * z ** 5 + 16 * z ** 3 + 3 * z) / (96 * v * v) + (3 * z ** 7 + 19 * z ** 5 + 17 * z ** 3 - 15 * z) / (384 * v ** 3)


def alpha(conf):
    return 1 - Q(repr(conf))


def meets_rate(x, n, conf, t):
    """Clopper-Pearson's bound of x successes in n reaches t."""
    if t <= 0:
        return True
    if n <= 0 or x <= 0 or t >= 1:
        return False
    tail, choose = Q(0), 1
    for j in range(0, n + 1):
        if j > 0:
            choose = choose * (n - j + 1) // j
        if j >= x:
            tail += choose * t ** j * (1 - t) ** (n - j)
    return tail <= alpha(conf)


def _variance(values, unseen, w, lag1):
    """se^2 of the values with `unseen` counted w times beside them; with lag1, the lag-1 autocovariance added, never lowering it."""
    n = len(values) + w
    mean = (sum(values, Q(0)) + w * unseen) / n
    g0 = (sum(((v - mean) ** 2 for v in values), Q(0)) + w * (unseen - mean) ** 2) / (n - 1)
    if not lag1:
        return mean, g0 / n
    g1 = sum(((values[i] - mean) * (values[i + 1] - mean) for i in range(len(values) - 1)), Q(0)) / (n - 1)
    return mean, max(g0, g0 + 2 * g1) / n


def _at_least(margin, q, variance):
    """margin >= q * sqrt(variance), q > 0, variance >= 0."""
    return margin >= 0 and margin * margin >= q * q * variance


def meets_clustered(units, cap, conf, t):
    if t <= 0:
        return True
    k = len(units)
    offered = sum(o for o, _ in units)
    if k == 0 or offered == 0 or t > 1:
        return False
    w = HEDGE[conf]
    r = Q(sum(c for _, c in units)) / (offered + w * cap)
    ob = (offered + w * cap) / (k + w)
    _, variance = _variance([c - r * o for o, c in units], -r * cap, w, True)
    # r - q se / ob >= t, ob > 0
    return _at_least((r - t) * ob, q_of(conf, k - 1), variance)


def meets_precision(tasks, cap, conf, t):
    """Fieller's bound (with the unseen task) of the pooled precision reaches t; Clopper-Pearson at a cap of 1."""
    if cap <= 1:
        return meets_rate(sum(1 for _, c in tasks if c > 0), sum(1 for o, _ in tasks if o > 0), conf, t)
    if t <= 0:
        return True
    n = len(tasks)
    if n == 0 or sum(o for o, _ in tasks) == 0 or t > 1:
        return False
    w = HEDGE[conf]
    N = n + w
    ob = (sum(o for o, _ in tasks) + w * cap) / N
    cb = Q(sum(c for _, c in tasks)) / N
    r = cb / ob
    soo = (sum(((o - ob) ** 2 for o, _ in tasks), Q(0)) + w * (cap - ob) ** 2) / (N - 1)
    scc = (sum(((c - cb) ** 2 for _, c in tasks), Q(0)) + w * cb ** 2) / (N - 1)
    sco = (sum(((o - ob) * (c - cb) for o, c in tasks), Q(0)) + w * (cap - ob) * (0 - cb)) / (N - 1)
    q = q_of(conf, n - 1)
    a = N * ob * ob - q * q * soo
    b = -2 * N * cb * ob + 2 * q * q * sco
    c = N * cb * cb - q * q * scc
    if a <= 0 or r < t:
        return False
    if b * b - 4 * a * c < 0:
        return True  # the bound is the estimate r, and r >= t
    return a * t * t + b * t + c >= 0 and 2 * a * t <= -b


def meets_precision_upper_below(tasks, cap, conf, t):
    """-(precision upper bound) reaches t: 1 - (the wrong items' lower bound) <= -t."""
    return meets_precision([(o, o - c) for o, c in tasks], cap, conf, 1 + t)


def meets_precision_diff(t1, cap1, t2, cap2, conf, t, lag1=False):
    if cap2 == 0:
        return t <= 0 if cap1 == 0 else meets_precision(t1, cap1, conf, t)
    if cap1 == 0:
        return meets_precision_upper_below(t2, cap2, conf, t)
    if t <= -1:
        return True
    if t > 1:
        return False
    w = HEDGE[conf]
    n = len(t1)
    o1 = sum(o for o, _ in t1) + w * cap1
    c1 = Q(sum(c for _, c in t1))
    o2 = sum(o for o, _ in t2) + w * cap2
    c2 = sum(c for _, c in t2) + w * cap2
    N = n + w
    r1, r2, m1, m2 = c1 / o1, c2 / o2, o1 / N, o2 / N
    f = [(a[1] - r1 * a[0]) / m1 - (b[1] - r2 * b[0]) / m2 for a, b in zip(t1, t2)]
    _, variance = _variance(f, (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2, w, lag1)
    return _at_least(r1 - r2 - t, q_of(conf, n - 1), variance)


def meets_hit_diff(t1, t2, conf, t):
    if t <= -1:
        return True
    if t > 1:
        return False
    d = [Q(1 if a[1] > 0 else 0) - Q(1 if b[1] > 0 else 0) for a, b in zip(t1, t2)]
    mean, variance = _variance(d, Q(-1), HEDGE[conf], False)
    return _at_least(mean - t, q_of(conf, len(t1) - 1), variance)


def cluster_size(spacing):
    s = min(max(spacing, 1), BURST)
    return (BURST + s - 1) // s


def clusters(tasks, m):
    return [(sum(o for o, _ in tasks[i:i + m]), sum(c for _, c in tasks[i:i + m])) for i in range(0, len(tasks), m)]


def hit_clusters(tasks, m):
    return [(len(tasks[i:i + m]), sum(1 for _, c in tasks[i:i + m] if c > 0)) for i in range(0, len(tasks), m)]


def meets_hit_rate_arm(tasks, spacing, conf, t):
    m = cluster_size(spacing)
    if m == 1:
        return meets_rate(sum(1 for _, c in tasks if c > 0), len(tasks), conf, t)
    return meets_clustered(hit_clusters(tasks, m), m, conf, t)


def meets_precision_arm(tasks, cap, spacing, conf, t):
    m = cluster_size(spacing)
    return meets_precision(tasks, cap, conf, t) if m == 1 else meets_clustered(clusters(tasks, m), m * cap, conf, t)


def meets_hit_diff_arm(t1, t2, spacing, conf, t):
    m = cluster_size(spacing)
    return meets_hit_diff(t1, t2, conf, t) if m == 1 else meets_precision_diff(hit_clusters(t1, m), m, hit_clusters(t2, m), m, conf, t, lag1=True)


def meets_precision_diff_arm(t1, cap1, t2, cap2, spacing, conf, t):
    m = cluster_size(spacing)
    if m == 1 or cap1 == 0 or cap2 == 0:
        if cap2 == 0:
            return t <= 0 if cap1 == 0 else meets_precision_arm(t1, cap1, spacing, conf, t)
        if cap1 == 0:
            return meets_precision_arm([(o, o - c) for o, c in t2], cap2, spacing, conf, 1 + t)
        return meets_precision_diff(t1, cap1, t2, cap2, conf, t)
    return meets_precision_diff(clusters(t1, m), m * cap1, clusters(t2, m), m * cap2, conf, t, lag1=True)
