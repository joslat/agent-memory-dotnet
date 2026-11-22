"""The Python runner of the shared native gate (NATIVE-GATE-SHARED). Standard library and the binding's loader only.

It reads ``native/gate/cases/*.json`` and drives the library through its C ABI (``am_v1_*``), the low-level job API
every binding has, so a case means the same thing in every language. The format is in ``native/gate/cases/FORMAT.md``;
a runner in another language implements the same steps and matchers. Run ``python gate_runner.py --tier A`` (or B, C),
or import it: ``native/gate/tests/test_cases.py`` turns each case into a unittest test.

Environment, as the gate's: AGENTMEMORY_NATIVE_LIBRARY (or an installed wheel), AMP_URI, AMP_USER, AMP_PASSWORD,
AMP_TLS_URI and AMP_TLS_OTHER_NAME_URI (the TLS cases), GATE_TIER.
"""
from __future__ import annotations

import base64
import copy
import ctypes
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
CASES = HERE.parent / "cases"
UNREACHABLE = "bolt://127.0.0.1:9"
TIERS = ("A", "B", "C")

try:
    from agentmemory_native import _library as abi
except ImportError:  # a checkout without the binding installed: the sources beside the gate
    sys.path.insert(0, str(HERE.parents[1] / "python"))
    from agentmemory_native import _library as abi

STATUS = {getattr(abi, name): name for name in ("OK", "INVALID_ARGUMENT", "UNSUPPORTED_ABI", "INVALID_HANDLE", "OVERLOADED",
                                                "CLOSING", "INTERNAL_ERROR", "BUSY")}


class CaseFailure(AssertionError):
    pass


class Skip(Exception):
    pass


# Loading ---------------------------------------------------------------------------------------------------------
def load_cases(tier: str | None = None) -> list[dict]:
    """Every case of the files in cases/, in file and list order; only ``tier``'s when given. Ids are unique."""
    found: list[dict] = []
    for path in sorted(CASES.glob("*.json")):
        if path.name == "fixtures.json":
            continue
        document = json.loads(path.read_text(encoding="utf-8"))
        for case in document["cases"]:
            case = dict(case, file=path.name)
            found.append(case)
    ids = [c["id"] for c in found]
    duplicates = {i for i in ids if ids.count(i) > 1}
    if duplicates:
        raise ValueError(f"case ids used twice: {sorted(duplicates)}")
    # Tier B runs on a seeded store, and the contract cases of tier A run there too (verified against it).
    wanted = {"A": {"A"}, "B": {"A", "B"}, "C": {"C"}}.get(tier or "", set(TIERS))
    return [c for c in found if c["tier"] in wanted]


def fixtures() -> dict:
    return json.loads((CASES / "fixtures.json").read_text(encoding="utf-8"))


# Variables and templates -------------------------------------------------------------------------------------------
class Environment:
    """What the cases may refer to with {"$var": name}: the store, the stubs, the tier, a fresh state directory."""

    def __init__(self, tier: str, stub_url: str | None) -> None:
        self.tier = tier
        store = tier != "A"
        self.vars: dict[str, Any] = {
            "tier": tier,
            "storeUri": os.environ.get("AMP_URI", UNREACHABLE) if store else UNREACHABLE,
            "storeUser": os.environ.get("AMP_USER", "neo4j"),
            "storePassword": os.environ.get("AMP_PASSWORD", "gate-tier-a-unused"),
            "verify": store,
            "tlsUri": os.environ.get("AMP_TLS_URI"),
            "tlsOtherNameUri": os.environ.get("AMP_TLS_OTHER_NAME_URI"),
            "stubUrl": stub_url,
        }

    def fresh_state(self) -> str:
        return str(Path(tempfile.mkdtemp(prefix="am-gate-state-")) / "state")


def resolve(value: Any, env: Environment, fx: dict, case_vars: dict) -> Any:
    """Replaces the placeholders of FORMAT.md, depth first."""
    if isinstance(value, list):
        return [resolve(v, env, fx, case_vars) for v in value]
    if not isinstance(value, dict):
        return value
    if "$var" in value:
        name, _, pointer = value["$var"].partition("/")
        if name in case_vars:
            return get_pointer(case_vars[name], "/" + pointer) if pointer else case_vars[name]
        if name not in env.vars:
            raise ValueError(f"unknown variable {name}")
        found = env.vars[name]
        if found is None:
            raise Skip(f"needs {name}, which this run does not have")
        return found
    if "$freshState" in value:
        return env.fresh_state()
    if "$vector" in value:
        return list(fx["vectors"][value["$vector"]])
    if "$config" in value:
        document = resolve(copy.deepcopy(fx["configs"][value["$config"]]), env, fx, case_vars)
        for pointer, replacement in (value.get("$with") or {}).items():
            set_pointer(document, pointer, resolve(replacement, env, fx, case_vars))
        for pointer in value.get("$without") or []:
            remove_pointer(document, pointer)
        return document
    if "$concat" in value:
        return "".join(str(resolve(part, env, fx, case_vars)) for part in value["$concat"])
    return {k: resolve(v, env, fx, case_vars) for k, v in value.items()}


def raw_bytes(spec: Any, env: Environment, fx: dict, case_vars: dict) -> bytes:
    """A request or configuration as bytes: JSON of a document, or exact bytes ($base64, $utf8, $repeat)."""
    if isinstance(spec, dict) and "$base64" in spec:
        return base64.b64decode(spec["$base64"])
    if isinstance(spec, dict) and "$utf8" in spec:
        return spec["$utf8"].encode("utf-8")
    if isinstance(spec, dict) and "$repeat" in spec:
        text, times = spec["$repeat"]
        return (text * times).encode("utf-8")
    if isinstance(spec, dict) and "$jsonReplace" in spec:
        document, old, new = spec["$jsonReplace"]
        return json.dumps(resolve(document, env, fx, case_vars)).replace(
            str(resolve(old, env, fx, case_vars)), new).encode("utf-8")
    return json.dumps(resolve(spec, env, fx, case_vars), ensure_ascii=False).encode("utf-8")


def split_pointer(pointer: str) -> list[str]:
    return [p.replace("~1", "/").replace("~0", "~") for p in pointer.lstrip("/").split("/")] if pointer not in ("", "/") else []


def set_pointer(document: Any, pointer: str, value: Any) -> None:
    parts = split_pointer(pointer)
    target = document
    for part in parts[:-1]:
        target = target[int(part)] if isinstance(target, list) else target.setdefault(part, {})
    target[parts[-1]] = value


def remove_pointer(document: Any, pointer: str) -> None:
    parts = split_pointer(pointer)
    target = document
    for part in parts[:-1]:
        target = target[part]
    target.pop(parts[-1], None)


MISSING = object()


def get_pointer(document: Any, pointer: str) -> Any:
    target = document
    for part in split_pointer(pointer):
        if isinstance(target, list):
            if not part.isdigit() or int(part) >= len(target):
                return MISSING
            target = target[int(part)]
        elif isinstance(target, dict):
            if part not in target:
                return MISSING
            target = target[part]
        else:
            return MISSING
    return target


# Matching ----------------------------------------------------------------------------------------------------------
def recalled_ids(result: Any) -> list[str]:
    """$ids: the ids of a recall's facts and preferences, sorted."""
    if not isinstance(result, dict):
        return []
    return sorted({f["id"] for f in result.get("facts") or []} | {p["id"] for p in result.get("preferences") or []})


def match(actual: Any, expected: Any, where: str = "") -> None:
    """Raises CaseFailure at the first difference. Objects match partially: keys the expectation leaves out may be
    there. Lists match whole and in order. Keys starting with "/" are JSON pointers; "$ids" is recalled_ids."""
    if isinstance(expected, dict) and expected and all(k.startswith("$") and k not in ("$ids",) for k in expected):
        for operator, operand in expected.items():
            operate(actual, operator, operand, where)
        return
    if isinstance(expected, dict):
        if not isinstance(actual, dict):
            raise CaseFailure(f"{where or '/'}: expected an object, got {short(actual)}")
        for key, wanted in expected.items():
            if key.startswith("$") and key != "$ids":
                operate(actual, key, wanted, where)  # an operator beside plain keys applies to the whole object
                continue
            if key == "$ids":
                match(recalled_ids(actual), sorted(wanted) if isinstance(wanted, list) else wanted, f"{where}/$ids")
                continue
            value = get_pointer(actual, key) if key.startswith("/") else actual.get(key, MISSING)
            path = f"{where}{key}" if key.startswith("/") else f"{where}/{key}"
            if isinstance(wanted, dict) and wanted.get("$absent"):
                if value is not MISSING:
                    raise CaseFailure(f"{path}: expected absent, got {short(value)}")
                continue
            if isinstance(wanted, dict) and wanted.get("$empty"):
                if value is not MISSING and value not in ([], {}, "", None):
                    raise CaseFailure(f"{path}: expected absent or empty, got {short(value)}")
                continue
            if value is MISSING:
                raise CaseFailure(f"{path}: missing (expected {short(wanted)})")
            match(value, wanted, path)
        return
    if isinstance(expected, list):
        if not isinstance(actual, list) or len(actual) != len(expected):
            raise CaseFailure(f"{where or '/'}: expected {short(expected)}, got {short(actual)}")
        for i, (a, e) in enumerate(zip(actual, expected)):
            match(a, e, f"{where}/{i}")
        return
    if isinstance(expected, bool) or isinstance(actual, bool):
        same = actual is expected
    elif isinstance(expected, (int, float)) and isinstance(actual, (int, float)):
        same = actual == expected
    else:
        same = actual == expected
    if not same:
        raise CaseFailure(f"{where or '/'}: expected {short(expected)}, got {short(actual)}")


def operate(actual: Any, operator: str, operand: Any, where: str) -> None:
    def fail(text: str) -> None:
        raise CaseFailure(f"{where or '/'}: {text}, got {short(actual)}")

    if operator == "$oneOf":
        for option in operand:
            try:
                match(actual, option, where)
                return
            except CaseFailure:
                pass
        fail(f"expected one of {short(operand)}")
    elif operator == "$not":
        try:
            match(actual, operand, where)
        except CaseFailure:
            return
        fail(f"expected anything but {short(operand)}")
    elif operator == "$contains":
        items = operand if isinstance(operand, list) else [operand]
        for item in items:
            if isinstance(actual, str):
                if item not in actual:
                    fail(f"expected text containing {short(item)}")
            elif isinstance(actual, list):
                if not any(_matches(a, item) for a in actual):
                    fail(f"expected a list containing {short(item)}")
            else:
                fail("expected text or a list")
    elif operator == "$notContains":
        items = operand if isinstance(operand, list) else [operand]
        for item in items:
            if (isinstance(actual, str) and item in actual) or (isinstance(actual, list) and any(_matches(a, item) for a in actual)):
                fail(f"expected nothing containing {short(item)}")
    elif operator == "$setEq":
        if not isinstance(actual, list) or sorted(map(json.dumps, actual)) != sorted(map(json.dumps, operand)):
            fail(f"expected the set {short(operand)}")
    elif operator == "$gt":
        if not isinstance(actual, (int, float)) or not actual > operand:
            fail(f"expected more than {operand}")
    elif operator == "$gte":
        if not isinstance(actual, (int, float)) or not actual >= operand:
            fail(f"expected at least {operand}")
    elif operator == "$textExcludes":
        if operand in json.dumps(actual, ensure_ascii=False):
            fail(f"expected no text {short(operand)} anywhere")
    elif operator == "$lt":
        if not isinstance(actual, (int, float)) or not actual < operand:
            fail(f"expected less than {operand}")
    elif operator == "$len":
        if not hasattr(actual, "__len__") or len(actual) != operand:
            fail(f"expected length {operand}")
    elif operator == "$type":
        types = {"string": str, "number": (int, float), "integer": int, "boolean": bool, "object": dict, "array": list,
                 "null": type(None)}
        if not isinstance(actual, types[operand]) or (operand in ("number", "integer") and isinstance(actual, bool)):
            fail(f"expected a {operand}")
    elif operator == "$startsWith":
        if not isinstance(actual, str) or not actual.startswith(operand):
            fail(f"expected text starting with {short(operand)}")
    elif operator == "$all":
        if not isinstance(actual, list) or not actual:
            fail("expected a non-empty list")
        for i, item in enumerate(actual):
            match(item, operand, f"{where}/{i}")
    elif operator == "$absent":
        if operand:
            fail("expected absent")
    else:
        raise ValueError(f"unknown matcher {operator}")


def _matches(actual: Any, expected: Any) -> bool:
    try:
        match(actual, expected)
        return True
    except CaseFailure:
        return False


def short(value: Any) -> str:
    text = json.dumps(value, default=str) if value is not MISSING else "(missing)"
    return text if len(text) <= 300 else text[:297] + "..."


# The stubs ---------------------------------------------------------------------------------------------------------
class Stubs:
    """native/gate/stubs.py, run as a child process, as a runner in another language runs it."""

    def __init__(self) -> None:
        self.process = subprocess.Popen([sys.executable, str(HERE.parent / "stubs.py")], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, text=True)
        self.url = f"http://127.0.0.1:{json.loads(self.process.stdout.readline())['port']}"

    def control(self, body: dict) -> dict:
        return self._call("POST", "/_stub/control", body)

    def seen(self) -> dict:
        return self._call("GET", "/_stub/seen", None)

    def _call(self, method: str, path: str, body: dict | None) -> dict:
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(self.url + path, data=data, method=method,
                                         headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            return json.loads(response.read())

    def close(self) -> None:
        self.process.stdin.close()
        try:
            self.process.wait(10)
        except subprocess.TimeoutExpired:
            self.process.kill()


# Running one case --------------------------------------------------------------------------------------------------
class Run:
    """The state of one case: its contexts and jobs by name, the last results, its variables."""

    def __init__(self, case: dict, env: Environment, fx: dict, stubs: Stubs | None) -> None:
        self.case, self.env, self.fx, self.stubs = case, env, fx, stubs
        self.lib = abi.load()
        self.contexts: dict[str, int] = {}
        self.jobs: dict[str, Any] = {}
        self.vars: dict[str, Any] = {"stateDir": env.fresh_state()}

    def resolve(self, value: Any) -> Any:
        return resolve(value, self.env, self.fx, self.vars)

    def context(self, step: dict) -> int:
        name = self.resolve(step.get("ctx", "ctx"))
        if isinstance(name, int):
            return name  # a raw handle, for the invalid-handle cases
        return self.contexts[name]

    # The ABI, one function each, as FORMAT.md describes them.
    def open(self, args: dict) -> dict:
        data = raw_bytes(args["raw"], self.env, self.fx, self.vars) if "raw" in args else \
            json.dumps(self.resolve(args["config"])).encode("utf-8")
        context, pointer, length = ctypes.c_uint64(0), ctypes.c_void_p(), ctypes.c_size_t(0)
        status = self.lib.am_v1_open(args.get("abiVersion", abi.ABI_VERSION), data, len(data), ctypes.byref(context),
                                     ctypes.byref(pointer), ctypes.byref(length))
        document = abi.take_buffer(self.lib, pointer, length)
        outcome = {"status": STATUS.get(status, status)}
        if status == abi.OK:
            self.contexts[args.get("as", "ctx")] = context.value
            outcome["handle"] = context.value
        elif document:
            error = json.loads(document).get("error") or {}
            outcome.update({"code": error.get("code"), "message": error.get("message", "")})
        return outcome

    def submit(self, args: dict) -> tuple[int, int]:
        data = raw_bytes(args["raw"], self.env, self.fx, self.vars) if "raw" in args else \
            json.dumps(self.resolve(args["request"])).encode("utf-8")
        job = ctypes.c_uint64(0)
        status = self.lib.am_v1_submit(self.context(args), data, len(data), int(args.get("timeoutMs", 0)), ctypes.byref(job))
        return status, job.value

    def poll(self, context: int, job: int, wait_ms: int) -> tuple[int, dict | None]:
        disposition, pointer, length = ctypes.c_uint32(abi.PENDING), ctypes.c_void_p(), ctypes.c_size_t(0)
        status = self.lib.am_v1_poll(context, job, wait_ms, ctypes.byref(disposition), ctypes.byref(pointer), ctypes.byref(length))
        data = abi.take_buffer(self.lib, pointer, length)
        if status != abi.OK or disposition.value != abi.TERMINAL or data is None:
            return status, None
        return status, json.loads(data.decode("utf-8"))

    def await_result(self, context: int, job: int, budget_s: float) -> dict:
        deadline = time.monotonic() + budget_s
        while True:
            status, result = self.poll(context, job, 100)
            if status != abi.OK:
                raise CaseFailure(f"am_v1_poll returned {STATUS.get(status, status)} while waiting")
            if result is not None:
                return result
            if time.monotonic() > deadline:
                raise CaseFailure(f"no result within {budget_s:.0f} s")

    def release(self, context: int, job: int) -> int:
        return self.lib.am_v1_job_release(context, job)

    def close(self, context: int, wait_s: float) -> int:
        deadline = time.monotonic() + wait_s
        while True:
            status = self.lib.am_v1_close(context)
            if status != abi.BUSY or time.monotonic() > deadline:
                return status
            time.sleep(0.01)

    # Steps.
    def step(self, step: dict) -> Any:
        action = next(k for k in step if k not in ("expect", "as", "note", "save"))
        args = step[action] if isinstance(step[action], dict) else {}
        if "as" in step:
            args = dict(args, **{"as": step["as"]})
        handler = getattr(self, "do_" + "".join("_" + c.lower() if c.isupper() else c for c in action), None)
        if handler is None:
            raise ValueError(f"unknown step {action}")
        outcome = handler(args)
        if "save" in step:
            self.vars[step["save"]] = outcome
        return outcome

    def do_abi(self, _: dict) -> dict:
        return {"version": self.lib.am_v1_abi_version(), "hasWait": abi.has_wait(self.lib)}

    def do_open(self, args: dict) -> dict:
        return self.open(args)

    def do_close(self, args: dict) -> dict:
        status = self.close(self.context(args), float(args.get("waitS", 10)))
        if status == abi.OK and isinstance(args.get("ctx", "ctx"), str):
            self.contexts.pop(args.get("ctx", "ctx"), None)
        return {"status": STATUS.get(status, status)}

    def do_submit(self, args: dict) -> dict:
        status, job = self.submit(args)
        if status == abi.OK:
            self.jobs[args.get("as", "job")] = (self.context(args), job)
        return {"status": STATUS.get(status, status)}

    def do_await(self, args: dict) -> dict:
        context, job = self.jobs[args.get("job", "job")]
        return self.await_result(context, job, float(args.get("budgetS", 30)))

    def do_call(self, args: dict) -> dict:
        status, job = self.submit(args)
        if status != abi.OK:
            raise CaseFailure(f"am_v1_submit returned {STATUS.get(status, status)}")
        context = self.context(args)
        try:
            return self.await_result(context, job, float(args.get("budgetS", 30)))
        finally:
            self.release(context, job)

    def do_poll(self, args: dict) -> dict:
        context, job = self.jobs[args.get("job", "job")]
        status, result = self.poll(context, job, int(args.get("waitMs", 0)))
        return {"status": STATUS.get(status, status), "result": result}

    def do_cancel(self, args: dict) -> dict:
        context, job = (self.context(args), args["handle"]) if "handle" in args else self.jobs[args.get("job", "job")]
        return {"status": STATUS.get(self.lib.am_v1_cancel(context, job), "?")}

    def do_release(self, args: dict) -> dict:
        context, job = self.jobs[args.get("job", "job")]
        return {"status": STATUS.get(self.release(context, job), "?")}

    def do_state(self, args: dict) -> dict:
        values = [ctypes.c_uint64(0) for _ in range(4)]
        flags = ctypes.c_uint32(0)
        status = self.lib.am_v1_state(self.context(args), *(ctypes.byref(v) for v in values), ctypes.byref(flags))
        f = flags.value
        return {"status": STATUS.get(status, status), "stateRevision": values[0].value, "policyEpoch": values[1].value,
                "deletionEpoch": values[2].value, "memoryRevision": values[3].value,
                "readAllowed": bool(f & abi.READ_ALLOWED), "admissionAllowed": bool(f & abi.ADMISSION_ALLOWED),
                "writeQuiescent": bool(f & abi.WRITE_QUIESCENT), "closing": bool(f & abi.CLOSING_FLAG),
                "erasePending": bool(f & abi.ERASE_PENDING)}

    def do_submit_many(self, args: dict) -> dict:
        """Submits ``count`` times; the admitted jobs are kept under ``as``, the refusals counted by status."""
        jobs, refused = [], {}
        for _ in range(int(args["count"])):
            status, job = self.submit(args)
            if status == abi.OK:
                jobs.append((self.context(args), job))
            else:
                refused[STATUS.get(status, status)] = refused.get(STATUS.get(status, status), 0) + 1
        self.jobs[args.get("as", "jobs")] = jobs
        return {"admitted": len(jobs), "refused": refused}

    def do_await_all(self, args: dict) -> dict:
        """Every job of a list to its result, each then released; the results in submit order."""
        results = []
        for context, job in self.jobs[args.get("jobs", "jobs")]:
            try:
                results.append(self.await_result(context, job, float(args.get("budgetS", 30))))
            finally:
                self.release(context, job)
        self.jobs[args.get("jobs", "jobs")] = []
        return {"results": results}

    def do_drain(self, args: dict) -> dict:
        """am_v1_wait until ``count`` jobs were reported (or ``budgetS`` passed); ``sameAsSubmitted`` compares them with
        the list named ``jobs``, each reported once."""
        context = self.context(args)
        reported: list[int] = []
        deadline = time.monotonic() + float(args.get("budgetS", 30))
        while len(reported) < int(args["count"]) and time.monotonic() < deadline:
            finished = ctypes.c_uint64(0)
            status = self.lib.am_v1_wait(context, int(args.get("waitMs", 1000)), ctypes.byref(finished))
            if status != abi.OK:
                raise CaseFailure(f"am_v1_wait returned {STATUS.get(status, status)}")
            if finished.value:
                reported.append(finished.value)
        submitted = sorted(job for _, job in self.jobs.get(args.get("jobs", "jobs"), []))
        results = []
        if args.get("takeResults"):
            for job in reported:
                status, result = self.poll(context, job, 0)
                results.append(result)
        return {"reported": len(reported), "sameAsSubmitted": sorted(reported) == submitted, "results": results}

    def do_wait_any(self, args: dict) -> dict:
        finished = ctypes.c_uint64(0)
        status = self.lib.am_v1_wait(self.context(args), int(args.get("waitMs", 0)), ctypes.byref(finished))
        return {"status": STATUS.get(status, status), "job": finished.value}

    def do_release_all(self, args: dict) -> dict:
        for context, job in self.jobs.get(args.get("jobs", "jobs"), []):
            self.release(context, job)
        self.jobs[args.get("jobs", "jobs")] = []
        return {}

    def do_repeat(self, args: dict) -> Any:
        """Runs ``step`` up to ``times`` times, until its outcome matches ``until``; the last outcome is the step's."""
        outcome = None
        for attempt in range(int(args["times"])):
            if attempt and args.get("sleepS"):
                time.sleep(float(args["sleepS"]))
            outcome = self.step(args["step"])
            if "until" in args and _matches(outcome, self.resolve(args["until"])):
                break
        return outcome

    def do_stub(self, args: dict) -> dict:
        if self.stubs is None:
            raise Skip("no stubs in this run")
        if args.get("seen"):
            return self.stubs.seen()
        return self.stubs.control(args)

    def do_set(self, args: dict) -> dict:
        """Case variables: {"set": {"name": value}}; a value may be {"$unique": "prefix"}."""
        for name, value in args.items():
            if isinstance(value, dict) and "$unique" in value:
                value = f"{value['$unique']}{os.urandom(8).hex()}"
            self.vars[name] = self.resolve(value)
        return {}

    def do_sleep(self, args: dict) -> dict:
        time.sleep(float(args.get("seconds", 0)))
        return {}

    def finish(self) -> None:
        if self.stubs is not None and self.case.get("stubs"):
            self.stubs.control({"mode": "ok", "release": True})  # a held write must not keep a close waiting
        for context, job in [j for js in self.jobs.values() for j in (js if isinstance(js, list) else [js])]:
            self.release(context, job)
        for context in self.contexts.values():
            self.close(context, 30)


def run_case(case: dict, env: Environment, fx: dict, stubs: Stubs | None) -> None:
    """Runs one case; raises Skip, or CaseFailure naming the step that failed."""
    for need in case.get("needs", []):
        if env.vars.get(need) is None:
            raise Skip(f"needs {need}")
    run = Run(case, env, fx, stubs)
    try:
        if stubs is not None and case.get("stubs"):
            stubs.control({"reset": True})
        for number, step in enumerate(case["steps"], 1):
            outcome = run.step(step)
            if "expect" in step:
                try:
                    match(outcome, run.resolve(step["expect"]))
                except CaseFailure as failure:
                    action = next(k for k in step if k not in ("expect", "as", "note", "save"))
                    raise CaseFailure(f"{case['id']}, step {number} ({action}): {failure}") from None
    finally:
        run.finish()


def main(argv: list[str]) -> int:
    tier = (argv[argv.index("--tier") + 1] if "--tier" in argv else os.environ.get("GATE_TIER", "A")).upper()
    fx = fixtures()
    stubs = Stubs()
    env = Environment(tier, stubs.url)
    failed = passed = skipped = 0
    try:
        for case in load_cases(tier):
            started = time.perf_counter()
            try:
                run_case(case, env, fx, stubs)
                passed += 1
                print(f"ok      {case['id']} ({(time.perf_counter() - started) * 1000:.0f} ms)", flush=True)
            except Skip as reason:
                skipped += 1
                print(f"skipped {case['id']}: {reason}", flush=True)
            except Exception as failure:  # noqa: BLE001 - every failure is reported, then the run goes on
                failed += 1
                print(f"FAIL    {case['id']}: {failure}", flush=True)
    finally:
        stubs.close()
    print(f"tier {tier}: {passed} passed, {failed} failed, {skipped} skipped", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
