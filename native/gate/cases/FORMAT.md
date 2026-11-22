# The gate's cases: format and runner contract

The native library's conformance gate as data (NATIVE-GATE-SHARED). Every binding (Python, TypeScript, Rust, Go)
runs these cases through its own low-level job API, on every RID it ships. A binding is done when it passes every case
of tiers A, B and C and has the tests `../REQUIRED.md` lists. The reference runner is `../runner/gate_runner.py`
(about 400 lines); `../check_mapping.py` proves no original test was lost.

## Files

| File | What |
|---|---|
| `contract.json` | Tier A: the contract, with no store |
| `recall.json` | Tier B: recall on the seeded store (`../seed/seed.cs`) |
| `writes.json` | Tier C: writes on their own store, with the stub model |
| `fixtures.json` | Named vectors and configuration templates |
| `ORIGINAL-TESTS.txt` | The Python tests before the move, for `check_mapping.py` |

Each case file is `{"format": ..., "description": ..., "cases": [case, ...]}`. Cases run in file order and list order.

## Tiers and the environment

| Tier | Store | Runs the cases of | Variables it sets |
|---|---|---|---|
| A | none: `storeUri` is `bolt://127.0.0.1:9`, `verify` is false | A | |
| B | the seeded read store | A and B (tier A's cases are verified against the store) | `AMP_URI`, `AMP_USER`, `AMP_PASSWORD`; `AMP_TLS_URI` and `AMP_TLS_OTHER_NAME_URI` for the TLS cases |
| C | a seeded store of its own (the cases write) | C | as B |

Order matters once: `store.wrong-password-is-reported-as-authentication` is the last case of tier B, because Neo4j
briefly locks a user after failed logins. Run cases in order, and run them after any other test of the binding that
opens the store.

## A case

```json
{
  "id": "recall.per-request-owner",
  "tier": "B",
  "source": "native/gate/tests/test_gate.py::Test3Recall::test_per_request_owner",
  "stubs": true,
  "needs": ["tlsUri"],
  "note": "why something is the way it is",
  "steps": [ {"open": {"config": {"$config": "read"}}, "expect": {"status": "OK"}}, ... ]
}
```

- `id`: unique, stable; `area.what-it-shows`.
- `source`: the original test it came from.
- `stubs`: the case uses the stub servers; the runner resets them before the case (`{"reset": true}`) and releases a
  held write after it (`{"mode": "ok", "release": true}`), so a failed case cannot keep a close waiting.
- `needs`: variables without which the case is skipped (reported as skipped, not passed).
- Each step has one action, an optional `expect` (matched against the step's outcome), an optional `save` (stores the
  outcome as a case variable) and an optional `as` (names the context or job it creates).

At the end of a case the runner releases the jobs it still holds and closes the contexts it still holds.

## Placeholders

Resolved, depth first, in step arguments and in expectations, before the step runs or is matched.

| Placeholder | Becomes |
|---|---|
| `{"$var": "name"}` | a case variable (from `save` or `set`) or an environment variable; `"name/pointer"` reads a JSON pointer inside it (`"applied/receipt/facts/0"`). An environment variable without a value skips the case |
| `{"$config": "read", "$with": {"/owner": {...}}, "$without": ["/writer"]}` | a configuration template of `fixtures.json`, with JSON pointers set and removed |
| `{"$vector": "near_axis_0"}` | a named vector of `fixtures.json` |
| `{"$concat": [a, b]}` | the parts as one string |
| `{"$freshState": true}` | a new, empty state directory path |
| `{"$unique": "prefix"}` (in `set` only) | the prefix and 16 random hexadecimal digits |

Environment variables: `tier`, `storeUri`, `storeUser`, `storePassword`, `verify`, `tlsUri`, `tlsOtherNameUri`,
`stubUrl`; and per case `stateDir`, a fresh state directory.

Bytes, where a step takes `raw` instead of a document: `{"$base64": "..."}`, `{"$utf8": "text"}` (the text's UTF-8
bytes, so `"\\ud800"` is six characters, an invalid escape for the library to refuse), `{"$repeat": [" ", 65537]}`,
`{"$jsonReplace": [document, old, new]}` (the document's JSON with one string replaced), or any document (its JSON).

## Steps

Statuses are the C ABI's names: `OK`, `INVALID_ARGUMENT`, `UNSUPPORTED_ABI`, `INVALID_HANDLE`, `OVERLOADED`,
`CLOSING`, `INTERNAL_ERROR`, `BUSY`. Results are the library's result documents, as JSON.

| Step | Arguments | Outcome |
|---|---|---|
| `abi` | | `{"version": am_v1_abi_version(), "hasWait": bool}` |
| `open` | `config` or `raw`; `as` (default `ctx`) | `{"status", "handle"}` when OK, else `{"status", "code", "message"}` from the error document |
| `close` | `ctx`, `waitS` (default 10): repeat `am_v1_close` while it says BUSY, until then | `{"status"}` |
| `submit` | `ctx`, `request` or `raw`, `timeoutMs` (default 0); `as` (default `job`) | `{"status"}`; the job is kept when OK |
| `await` | `job`, `budgetS` (default 30): poll until terminal | the result document |
| `call` | as `submit`, then `await`, then release | the result document (a refused submit fails the case) |
| `poll` | `job`, `waitMs` (default 0) | `{"status", "result"}`; `result` is null until terminal |
| `cancel` | `job`, or `ctx` and a raw `handle` | `{"status"}` |
| `release` | `job` | `{"status"}` |
| `state` | `ctx` | `{"status", "stateRevision", "policyEpoch", "deletionEpoch", "memoryRevision", "readAllowed", "admissionAllowed", "writeQuiescent", "closing", "erasePending"}` |
| `submitMany` | `count`, as `submit`; `as` (default `jobs`) | `{"admitted": n, "refused": {"STATUS": n}}` |
| `awaitAll` | `jobs`: each to its result, then released | `{"results": [...]}` in submit order |
| `drain` | `count`, `waitMs` (default 1000), `budgetS`, `jobs`, `takeResults`: `am_v1_wait` until `count` were reported | `{"reported": n, "sameAsSubmitted": bool, "results": [...]}` (each reported job's result when `takeResults`) |
| `waitAny` | `waitMs` | `{"status", "job"}` (0 when none) |
| `releaseAll` | `jobs` | `{}` |
| `repeat` | `step`, `times`, `until` (a match), `sleepS` between tries | the last outcome |
| `stub` | `{"seen": true}`, or control: `mode` (`ok`, `fail`, `hold`), `release`, `reset` | what the stubs saw, or `{"mode"}` |
| `set` | `{"name": value}` | `{}`; sets case variables |
| `sleep` | `seconds` | `{}` |

`ctx` defaults to `ctx`; it may be a number (a raw handle, for the invalid-handle cases) or a placeholder.

## Matching

- An object matches partially: keys the expectation leaves out may be there. A key starting with `/` is a JSON pointer
  into the outcome; `$ids` is the sorted ids of a recall's `facts` and `preferences`.
- A list matches whole and in order; anything else matches by equality (`true` is not `1`).
- An object whose keys start with `$` is a matcher. Matchers may also sit beside plain keys, and then apply to the whole
  object.

| Matcher | Holds when |
|---|---|
| `$oneOf: [a, b]` | any of them matches |
| `$not: x` | x does not match |
| `$contains: x` or `[x, y]` | text contains each, or a list has an element matching each |
| `$notContains: x` or `[x, y]` | the opposite, for each |
| `$setEq: [...]` | the list holds the same elements, in any order |
| `$len: n` | the length is n |
| `$gt`, `$gte`, `$lt` | numeric comparisons |
| `$type` | `string`, `number`, `integer`, `boolean`, `object`, `array`, `null` |
| `$startsWith: "text"` | text starts with it |
| `$all: x` | a non-empty list whose every element matches x |
| `$absent: true` | the key is not there |
| `$empty: true` | the key is not there, or is empty |
| `$textExcludes: "text"` | the value's JSON contains no such text |

## The stubs

`../stubs.py`, standard library only. Start it (`python stubs.py`), read its first line (`{"port": N}`), and close its
input to stop it. It serves the read gate's embedder (`/axes/v1/embeddings`), the write gate's (`/letters/v1/embeddings`),
the writer and the judge (`/v1/chat/completions`), what it saw (`GET /_stub/seen`) and its control
(`POST /_stub/control`). Every runner uses this script, so no language reimplements the stubs.

## Adding a case

Write it in the file of its tier, with a unique id and the source it comes from (a new test with no Python original
names itself: `"source": "native/gate/cases/<file>::<id>"`, which `check_mapping.py` accepts for cases only). Run the
reference runner on tiers A, B and C (`python runner/gate_runner.py --tier B`), then `python check_mapping.py`.
