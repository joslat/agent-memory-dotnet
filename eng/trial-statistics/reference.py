"""The reference for the trial bounds (statistic agentmemory.trial.hedged/2); the C# port in PackTrialGate must agree (golden.json).

Clopper-Pearson for a hit rate, and for precision when an arm offers one item a task at most. Otherwise the unseen worst-case
task, counted HEDGE[confidence] times beside the tasks (a frequency weight), then Fieller's inversion of the pooled precision,
or the paired difference task by task, at Student's t with tasks - 1 degrees of freedom. An arm with a cap of 0 offers nothing
by design (the baseline none): it has no uncertainty. Tasks drawn closer than BURST eligible commits apart (`spacing`) are
clustered first, in runs of ceil(BURST / spacing) in the order drawn (review 6, H4).
"""
import math
from tq import Z, student_q

# The least weight, in quarters, at which a paired hit difference at its target passes at most the nominal rate, for every n
# from 10 to 200 and every pair of rates, by exact enumeration (hitdiff_exact.py).
HEDGE = {0.9: 1.25, 0.95: 1.5, 0.975: 1.75, 0.99: 2.0}

# The most eligible commits one burst of related changes is taken to span (serial.py).
BURST = 8


def rate_lower(x, n, conf):
    """Clopper-Pearson: the rate at which P(X >= x) = 1 - conf; 0 for no successes."""
    if n <= 0 or x <= 0:
        return 0.0
    d = 1 - conf
    logc = [0.0] * (n + 1)
    for j in range(1, n + 1):
        logc[j] = logc[j - 1] + math.log(n - j + 1) - math.log(j)
    lo, hi = 0.0, 1.0
    for _ in range(64):
        p = (lo + hi) / 2
        lp, lq = math.log(p), math.log1p(-p)
        terms = [logc[j] + j * lp + (n - j) * lq for j in range(x, n + 1)]
        m = max(terms)
        tail = math.exp(m) * sum(math.exp(t - m) for t in terms)
        if tail > d:
            hi = p
        else:
            lo = p
    return lo


def _weighted(values, unseen, w):
    """Mean and standard error of the values with `unseen` counted w times beside them."""
    n = len(values) + w
    mean = (sum(values) + w * unseen) / n
    var = (sum((v - mean) ** 2 for v in values) + w * (unseen - mean) ** 2) / (n - 1)
    return mean, math.sqrt(var / n)


def precision_lower(tasks, cap, conf):
    """tasks: [(offered, correct)]. Cap 1: Clopper-Pearson over the offering tasks. Else Fieller with the unseen task (cap, 0)
    counted HEDGE times."""
    if cap <= 1:
        return rate_lower(sum(1 for _, c in tasks if c > 0), sum(1 for o, _ in tasks if o > 0), conf)
    n = len(tasks)
    if n == 0 or sum(o for o, _ in tasks) == 0:
        return 0.0
    w = HEDGE[conf]
    N = n + w
    ob = (sum(o for o, _ in tasks) + w * cap) / N
    cb = sum(c for _, c in tasks) / N
    r = cb / ob
    soo = (sum((o - ob) ** 2 for o, _ in tasks) + w * (cap - ob) ** 2) / (N - 1)
    scc = (sum((c - cb) ** 2 for _, c in tasks) + w * cb ** 2) / (N - 1)
    sco = (sum((o - ob) * (c - cb) for o, c in tasks) + w * (cap - ob) * (0 - cb)) / (N - 1)
    q = student_q(Z[conf], n - 1)
    a = N * ob * ob - q * q * soo
    b = -2 * N * cb * ob + 2 * q * q * sco
    cc = N * cb * cb - q * q * scc
    if a <= 0:
        return 0.0
    disc = b * b - 4 * a * cc
    if disc < 0:
        return min(max(r, 0.0), 1.0)
    root = math.sqrt(disc)
    lo = 2 * cc / (-b + root) if -b + root > 0 else (-b - root) / (2 * a)
    return min(max(lo, 0.0), r, 1.0)


def precision_upper(tasks, cap, conf):
    return 1 - precision_lower([(o, o - c) for o, c in tasks], cap, conf)


def precision_diff_lower(t1, cap1, t2, cap2, conf):
    """R1 - R2, paired: the unseen task, the first arm offering cap1 all wrong and the second cap2 all right, counted HEDGE
    times. A cap of 0 offers nothing by design."""
    if cap2 == 0:
        return 0.0 if cap1 == 0 else precision_lower(t1, cap1, conf)
    if cap1 == 0:
        return -precision_upper(t2, cap2, conf)
    w = HEDGE[conf]
    n = len(t1)
    o1 = sum(o for o, _ in t1) + w * cap1; c1 = sum(c for _, c in t1)
    o2 = sum(o for o, _ in t2) + w * cap2; c2 = sum(c for _, c in t2) + w * cap2
    N = n + w
    r1, r2, m1, m2 = c1 / o1, c2 / o2, o1 / N, o2 / N
    f = [(a[1] - r1 * a[0]) / m1 - (b[1] - r2 * b[0]) / m2 for a, b in zip(t1, t2)]
    _, se = _weighted(f, (0 - r1 * cap1) / m1 - (cap2 - r2 * cap2) / m2, w)
    q = student_q(Z[conf], n - 1)
    return min(max(r1 - r2 - q * se, -1.0), 1.0)


def hit_diff_lower(t1, t2, conf):
    """Hit rate 1 - hit rate 2, paired: the unseen task the first misses and the second hits, counted HEDGE times."""
    d = [(1.0 if a[1] > 0 else 0.0) - (1.0 if b[1] > 0 else 0.0) for a, b in zip(t1, t2)]
    mean, se = _weighted(d, -1.0, HEDGE[conf])
    q = student_q(Z[conf], len(t1) - 1)
    return min(max(mean - q * se, -1.0), 1.0)


def cluster_size(spacing):
    s = min(max(spacing, 1), BURST)
    return (BURST + s - 1) // s


def clusters(tasks, m):
    return [(sum(o for o, _ in tasks[i:i + m]), sum(c for _, c in tasks[i:i + m])) for i in range(0, len(tasks), m)]


def hit_clusters(tasks, m):
    return [(len(tasks[i:i + m]), sum(1 for _, c in tasks[i:i + m] if c > 0)) for i in range(0, len(tasks), m)]


def hit_rate_lower_arm(tasks, spacing, conf):
    """An arm's hit-rate bound as its trial is checked: Clopper-Pearson, or the clustered bound over runs of tasks."""
    m = cluster_size(spacing)
    if m == 1:
        return rate_lower(sum(1 for _, c in tasks if c > 0), len(tasks), conf)
    return precision_lower(hit_clusters(tasks, m), m, conf)


def precision_lower_arm(tasks, cap, spacing, conf):
    m = cluster_size(spacing)
    return precision_lower(tasks, cap, conf) if m == 1 else precision_lower(clusters(tasks, m), m * cap, conf)


def hit_diff_lower_arm(t1, t2, spacing, conf):
    m = cluster_size(spacing)
    return hit_diff_lower(t1, t2, conf) if m == 1 else precision_diff_lower(hit_clusters(t1, m), m, hit_clusters(t2, m), m, conf)


def precision_diff_lower_arm(t1, cap1, t2, cap2, spacing, conf):
    m = cluster_size(spacing)
    if m == 1 or cap1 == 0 or cap2 == 0:
        if cap2 == 0:
            return 0.0 if cap1 == 0 else precision_lower_arm(t1, cap1, spacing, conf)
        if cap1 == 0:
            return -(1 - precision_lower_arm([(o, o - c) for o, c in t2], cap2, spacing, conf))
        return precision_diff_lower(t1, cap1, t2, cap2, conf)
    return precision_diff_lower(clusters(t1, m), m * cap1, clusters(t2, m), m * cap2, conf)
