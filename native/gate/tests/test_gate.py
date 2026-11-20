"""G-AOT: the published native library, loaded from Python on the target image, against a real seeded Neo4j.

Standard library only (the target image has nothing else). Classes run in name order; failures that touch the
store's authentication run last, because Neo4j briefly locks a user after failed logins.
Environment: AGENTMEMORY_NATIVE_LIBRARY, AMP_URI, AMP_USER, AMP_PASSWORD. Fixture: native/gate/seed/seed.cs.
"""
from __future__ import annotations

import asyncio
import ctypes
import json
import os
import shutil
import statistics
import time
import unittest

from agentmemory_native import AsyncMemoryClient, MemoryClient, NativeCallError, OpenError
from agentmemory_native import _library as abi

DIMENSIONS = 8
OWNER_A = "gate-owner-a"
OWNER_B = "gate-owner-b"
NEAR_AXIS_0 = [1.0, 0.1, 0, 0, 0, 0, 0, 0]
A_IDS = {"gate-fact-a-work", "gate-fact-a-colour", "gate-pref-a-concise"}
B_IDS = {"gate-fact-b-work", "gate-pref-b-detail"}
COLOUR_METADATA = {
    "trust_level": "verified", "source": "gate-seed", "count": 3, "ratio": 2.5,
    "flags": [True, None, "x"], "nested": {"depth": 2, "label": "Zürich ✓"},
}


def config(*, mode: str = "fixed", owner: str = OWNER_A, uri: str | None = None, password: str | None = None,
           verify: bool = True, limits: dict | None = None) -> dict:
    document = {
        "schemaVersion": 1,
        "profile": "read_only",
        "owner": {"mode": mode, "id": owner} if mode == "fixed" else {"mode": mode},
        "store": {
            "uri": uri or os.environ["AMP_URI"],
            "username": os.environ["AMP_USER"],
            "password": password if password is not None else os.environ["AMP_PASSWORD"],
            "verifyConnectivity": verify,
        },
        "embedding": {"dimensions": DIMENSIONS, "model": "gate-unit-axes"},
    }
    if limits:
        document["limits"] = limits
    return document


def ids(result) -> set[str]:
    return {f.id for f in result.facts} | {p.id for p in result.preferences}


def thaw(value):
    if isinstance(value, tuple):
        return [thaw(v) for v in value]
    if hasattr(value, "items"):
        return {k: thaw(v) for k, v in value.items()}
    return value


def submit_raw(client: MemoryClient, data: bytes, timeout_ms: int = 0):
    job = ctypes.c_uint64(0)
    status = client._lib.am_v1_submit(client.handle, data, len(data), timeout_ms, ctypes.byref(job))
    return status, job.value


def result_of(client: MemoryClient, data: bytes) -> dict:
    status, handle = submit_raw(client, data)
    if status != abi.OK:
        raise NativeCallError("am_v1_submit", status)
    from agentmemory_native.client import Job
    with Job(client, handle) as job:
        return job.wait(10)


class Test1Library(unittest.TestCase):
    def test_loads_without_dotnet_and_reports_abi_1_0(self):
        self.assertIsNone(shutil.which("dotnet"), "the target image must not have .NET")
        self.assertFalse(os.path.exists("/usr/share/dotnet") or os.path.exists("/usr/lib/dotnet"))
        self.assertEqual(abi.load().am_v1_abi_version(), 0x00010000)

    def test_freeing_null_is_a_no_op(self):
        abi.load().am_v1_buffer_free(None)

    def test_invalid_handles_are_refused_not_crashing(self):
        lib = abi.load()
        flags = ctypes.c_uint32()
        values = [ctypes.c_uint64() for _ in range(4)]
        self.assertEqual(lib.am_v1_state(12345, *(ctypes.byref(v) for v in values), ctypes.byref(flags)), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_close(12345), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_cancel(12345, 1), abi.INVALID_HANDLE)


class Test2Open(unittest.TestCase):
    def test_capabilities_and_state(self):
        with MemoryClient(config()) as memory:
            caps = memory.capabilities()
            self.assertEqual(caps["status"], "ok")
            self.assertEqual(caps["profile"], "read_only")
            self.assertEqual(caps["abi"], {"major": 1, "minor": 0})
            self.assertEqual(caps["embedding"]["dimensions"], DIMENSIONS)
            self.assertIn("memory_get_context", caps["operations"])
            state = memory.state()
            self.assertTrue(state.read_allowed)
            self.assertFalse(state.closing)

    def test_one_context_per_store_until_it_closes(self):
        first = MemoryClient(config())
        try:
            with self.assertRaises(OpenError) as refused:
                MemoryClient(config(owner=OWNER_B))
            self.assertEqual(refused.exception.status, abi.BUSY)
            self.assertEqual(refused.exception.code, "authority_held")
        finally:
            first.close()
        MemoryClient(config()).close()

    def test_open_close_open_repeatedly(self):
        for _ in range(5):
            with MemoryClient(config()) as memory:
                self.assertTrue(memory.get_context(session_id="loop", query_vector=NEAR_AXIS_0, min_score=0.0).ok)

    def test_bad_configuration_is_refused_with_a_reason(self):
        broken = config()
        broken["store"]["surprise"] = 1
        with self.assertRaises(OpenError) as refused:
            MemoryClient(broken)
        self.assertEqual(refused.exception.status, abi.INVALID_ARGUMENT)
        self.assertEqual(refused.exception.code, "unknown_property")


class Test3Recall(unittest.TestCase):
    def test_owned_recall_decodes_facts_preferences_and_metadata(self):
        with MemoryClient(config()) as memory:
            result = memory.get_context(session_id="s1", query_vector=NEAR_AXIS_0, min_score=0.0, request_id="r-1")
        self.assertEqual(result.status, "ok")
        self.assertEqual(result.request_id, "r-1")
        self.assertEqual(ids(result), A_IDS)
        facts = {f.id: f for f in result.facts}
        self.assertEqual(facts["gate-fact-a-work"].object, "Zurich")
        self.assertEqual(thaw(facts["gate-fact-a-work"].metadata), {})
        self.assertEqual(thaw(facts["gate-fact-a-colour"].metadata), COLOUR_METADATA)
        self.assertEqual(thaw(result.preferences[0].metadata), {"source": "gate-seed"})
        self.assertEqual(result.facts[0].id, "gate-fact-a-work", "the nearest fact comes first")

    def test_fixed_owner_contexts_see_only_their_owner(self):
        with MemoryClient(config(owner=OWNER_A)) as memory:
            a = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0)
        with MemoryClient(config(owner=OWNER_B)) as memory:
            b = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0)
        self.assertEqual(ids(a), A_IDS)
        self.assertEqual(ids(b), B_IDS)

    def test_per_request_owner(self):
        with MemoryClient(config(mode="per_request")) as memory:
            b = memory.get_context(session_id="s", owner=OWNER_B, query_vector=NEAR_AXIS_0, min_score=0.0)
            nobody = memory.get_context(session_id="s", owner="gate-owner-nobody", query_vector=NEAR_AXIS_0, min_score=0.0)
            missing = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0)
        self.assertEqual(ids(b), B_IDS)
        self.assertEqual((nobody.status, ids(nobody)), ("ok", set()))
        self.assertEqual((missing.status, missing.error.code), ("invalid", "owner_required"))

    def test_fixed_owner_refuses_a_request_that_names_an_owner(self):
        with MemoryClient(config()) as memory:
            result = memory.get_context(session_id="s", owner=OWNER_B, query_vector=NEAR_AXIS_0)
        self.assertEqual((result.status, result.error.code), ("invalid", "owner_not_allowed"))
        self.assertEqual(ids(result), set())

    def test_min_score_and_limits_apply(self):
        with MemoryClient(config()) as memory:
            strict = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.9, max_preferences=0)
            one = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0, max_facts=1, max_preferences=0)
        self.assertEqual(ids(strict), {"gate-fact-a-work"})
        self.assertEqual(ids(one), {"gate-fact-a-work"})


class Test4Contract(unittest.TestCase):
    def test_malformed_requests_end_as_invalid_without_effects(self):
        cases = {
            b'{"schemaVersion":1,"operation":"capabilities_get","operation":"capabilities_get"}': "malformed_json",
            b'{"schemaVersion":1,"operation":"capabilities_get","extra":true}': "unknown_property",
            b'{"schemaVersion":2,"operation":"capabilities_get"}': "unsupported_schema",
            b'{"schemaVersion":1,"operation":"memory_forget_everything"}': "unknown_operation",
            b'\xff\xfe{"schemaVersion":1}': "malformed_json",
            b'[1,2]': "not_an_object",
            json.dumps({"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s",
                        "queryVector": [1, 2, 3]}).encode(): "dimension_mismatch",
            (b'{"schemaVersion":1,"operation":"memory_get_context","sessionId":"s",'
             b'"queryVector":[1,0,0,0,0,0,0,0],"x":{"a":1,"a":2}}'): "malformed_json",
            json.dumps({"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s",
                        "queryVector": NEAR_AXIS_0, "nested": {"a": 1}}).encode(): "unknown_property",
        }
        with MemoryClient(config()) as memory:
            for data, code in cases.items():
                with self.subTest(code=code, data=data[:40]):
                    result = result_of(memory, data)
                    self.assertEqual(result["status"], "invalid")
                    self.assertEqual(result["error"]["code"], code)

    def test_what_cannot_become_a_job_is_refused_at_submit(self):
        with MemoryClient(config()) as memory:
            self.assertEqual(submit_raw(memory, b"")[0], abi.INVALID_ARGUMENT)
            self.assertEqual(submit_raw(memory, b" " * (64 * 1024 + 1))[0], abi.INVALID_ARGUMENT)


class Test5Lifecycle(unittest.TestCase):
    def test_timeout_retires_the_result(self):
        with MemoryClient(config()) as memory:
            result = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, timeout_ms=1)
        self.assertEqual((result.status, result.error.code), ("timeout", "timeout"))

    def test_cancel_then_release_then_the_handle_is_dead(self):
        with MemoryClient(config()) as memory:
            job = memory.submit({"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s",
                                 "queryVector": NEAR_AXIS_0}, timeout_ms=5000)
            job.cancel()
            result = job.wait(10)
            self.assertIn(result["status"], ("cancelled", "ok"))
            job.release()
            with self.assertRaises(NativeCallError) as dead:
                job.poll()
            self.assertEqual(dead.exception.status, abi.INVALID_HANDLE)

    def test_repeated_terminal_polls_return_independent_copies(self):
        with MemoryClient(config()) as memory:
            with memory.submit({"schemaVersion": 1, "operation": "capabilities_get"}) as job:
                first = job.wait(10)
                second = job.poll()
                self.assertEqual(first, second)

    def test_overload_is_refused_at_submit_and_capacity_returns(self):
        with MemoryClient(config(limits={"maxRunningJobs": 1, "maxPendingJobs": 2})) as memory:
            request = {"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s", "queryVector": NEAR_AXIS_0}
            jobs, refused = [], 0
            for _ in range(20):
                try:
                    jobs.append(memory.submit(request, timeout_ms=5000))
                except NativeCallError as error:
                    self.assertEqual(error.status, abi.OVERLOADED)
                    refused += 1
            self.assertGreater(refused, 0)
            for job in jobs:
                with job:
                    self.assertEqual(job.wait(10)["status"], "ok")
            self.assertTrue(memory.get_context(session_id="s", query_vector=NEAR_AXIS_0).ok, "capacity came back")

    def test_closed_context_refuses_everything(self):
        memory = MemoryClient(config())
        handle = memory.handle
        memory.close()
        lib = abi.load()
        data = b'{"schemaVersion":1,"operation":"capabilities_get"}'
        job = ctypes.c_uint64()
        self.assertEqual(lib.am_v1_submit(handle, data, len(data), 0, ctypes.byref(job)), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_close(handle), abi.INVALID_HANDLE)

    def test_async_client_and_task_cancellation(self):
        async def scenario():
            async with await AsyncMemoryClient.open(config()) as memory:
                ok = await memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0)
                many = await asyncio.gather(*(memory.get_context(session_id=f"s{i}", query_vector=NEAR_AXIS_0, min_score=0.0)
                                              for i in range(16)))
                task = asyncio.create_task(memory.get_context(session_id="c", query_vector=NEAR_AXIS_0, timeout_ms=5000))
                await asyncio.sleep(0)
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
                return ok, many

        ok, many = asyncio.run(scenario())
        self.assertEqual(ids(ok), A_IDS)
        self.assertTrue(all(ids(r) == A_IDS for r in many))

    def test_many_recalls_keep_memory_bounded(self):
        def rss_kib() -> int:
            with open("/proc/self/status", encoding="ascii") as status:
                return next(int(line.split()[1]) for line in status if line.startswith("VmRSS:"))

        with MemoryClient(config()) as memory:
            for _ in range(50):
                memory.get_context(session_id="warm", query_vector=NEAR_AXIS_0, min_score=0.0)
            before = rss_kib()
            latencies = []
            for i in range(500):
                start = time.perf_counter()
                result = memory.get_context(session_id=f"m{i % 7}", query_vector=NEAR_AXIS_0, min_score=0.0)
                latencies.append((time.perf_counter() - start) * 1000)
                self.assertEqual(ids(result), A_IDS)
            after = rss_kib()
        latencies.sort()
        p50, p95 = statistics.median(latencies), latencies[int(len(latencies) * 0.95) - 1]
        print(f"\nMETRIC recall_p50_ms={p50:.2f} recall_p95_ms={p95:.2f} rss_growth_kib={after - before}", flush=True)
        self.assertLess(after - before, 64 * 1024, "500 recalls must not grow the process by 64 MiB")


class Test9StoreFailures(unittest.TestCase):
    def test_unreachable_store_fails_open_with_a_reason(self):
        with self.assertRaises(OpenError) as refused:
            MemoryClient(config(uri="bolt://am-gate-nowhere.invalid:7687"))
        self.assertEqual(refused.exception.code, "store_unavailable")

    def test_unreachable_store_without_verification_reports_unavailable_per_request(self):
        memory = MemoryClient(config(uri="bolt://am-gate-nowhere.invalid:7687", verify=False))
        try:
            result = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, timeout_ms=500)
        finally:
            start = time.monotonic()
            memory.close(timeout_s=30)
            settle_s = time.monotonic() - start
        print(f"\nMETRIC unreachable_status={result.status} close_after_deadline_s={settle_s:.2f}", flush=True)
        self.assertIn(result.status, ("unavailable", "timeout"))
        self.assertLess(settle_s, 10, "the driver bounds must end abandoned work within seconds")

    def test_wrong_password_is_reported_as_authentication(self):
        with self.assertRaises(OpenError) as refused:
            MemoryClient(config(password="not-the-password"))
        self.assertEqual(refused.exception.code, "authentication")
        self.assertNotIn("not-the-password", str(refused.exception))


if __name__ == "__main__":
    unittest.main(verbosity=2)
