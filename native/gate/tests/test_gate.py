"""G-AOT: the published native library, loaded from Python on the target image, against a real seeded Neo4j.

Standard library only (the target image has nothing else). Classes run in name order; failures that touch the
store's authentication run last, because Neo4j briefly locks a user after failed logins.
Environment: AGENTMEMORY_NATIVE_LIBRARY, AMP_URI, AMP_USER, AMP_PASSWORD. Fixture: native/gate/seed/seed.cs.
GATE_TIER=A runs only what needs no store, on any platform (NATIVE-PLAT-M1): no AMP_* variables are needed, a context is
opened against a closed local port without connecting, and every test that reads the seeded store is skipped with its reason.
"""
from __future__ import annotations

import asyncio
import ctypes
import http.server
import json
import os
import shutil
import socketserver
import statistics
import threading
import time
import unittest

import glob
import hashlib
import importlib.metadata
import platform
import subprocess
import sys
import tempfile
from pathlib import Path

from agentmemory_native import AsyncMemoryClient, MemoryClient, NativeCallError, NativeLibraryError, OpenError, manifest
from agentmemory_native import _library as abi

# Tier A: the build runners of every platform, where there is no store (NATIVE-PLAT-M1). Tier B: the same runners with a
# local seeded store (native/gate/store/store_gate.py, NATIVE-PLAT-STORE). Unset: the target image, with no .NET.
TIER = os.environ.get("GATE_TIER", "").upper()
TIER_A = TIER == "A"
ON_A_BUILD_RUNNER = TIER in ("A", "B")
UNREACHABLE = "bolt://127.0.0.1:9"
if TIER_A:
    os.environ.setdefault("AMP_URI", UNREACHABLE)
    os.environ.setdefault("AMP_USER", "neo4j")
    os.environ.setdefault("AMP_PASSWORD", "gate-tier-a-unused")
needs_store = unittest.skipIf(TIER_A, "tier A: needs the seeded store")
# A store that also takes TLS, with a certificate for "localhost" only, issued by an authority this machine trusts.
TLS_URI = os.environ.get("AMP_TLS_URI")
TLS_OTHER_NAME_URI = os.environ.get("AMP_TLS_OTHER_NAME_URI")
needs_tls = unittest.skipUnless(TLS_URI and TLS_OTHER_NAME_URI, "no TLS store: store_gate.py run --tls sets AMP_TLS_URI")

DIMENSIONS = 8
OWNER_A = "gate-owner-a"
OWNER_B = "gate-owner-b"
OWNER_C = "gate-owner-c"  # "Anna | sister of | user" and "Anna | lives in | Madrid", on axes 2 and 5
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


def opened(**kwargs) -> dict:
    """A configuration whose context needs no answer from the store: verified against it in the store tiers, not in tier A."""
    return config(verify=not TIER_A, **kwargs)


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
    def test_loads_and_reports_abi_1_0(self):
        self.assertEqual(abi.load().am_v1_abi_version(), 0x00010000)

    @unittest.skipIf(ON_A_BUILD_RUNNER, "tiers A and B run on a build runner, which has .NET")
    def test_the_target_image_has_no_dotnet(self):
        self.assertIsNone(shutil.which("dotnet"), "the target image must not have .NET")
        self.assertFalse(os.path.exists("/usr/share/dotnet") or os.path.exists("/usr/lib/dotnet"))

    def test_the_library_comes_from_a_known_place(self):
        path = abi.resolve_library_path()
        explicit = os.environ.get("AGENTMEMORY_NATIVE_LIBRARY")
        if explicit:
            self.assertEqual(str(path), os.path.realpath(explicit))
        else:
            self.assertEqual(path.parent.name, "_native", "an installed wheel loads the library it carries")
            self.assertEqual(path.parent.parent.name, "agentmemory_native")

    def test_an_unloadable_file_is_refused_with_an_actionable_error(self):
        with tempfile.TemporaryDirectory() as folder:
            fake = Path(folder, "agentmemory_native.so")
            fake.write_bytes(b"\x7fELF but not really a library")
            with self.assertRaises(NativeLibraryError) as refused:
                abi.load(fake)
        message = str(refused.exception)
        # The resolved path: on Windows a temporary folder can come back in its short (8.3) form.
        self.assertIn(fake.name, message)
        self.assertIn(str(fake.resolve()) if os.name == "nt" else str(fake), message)
        self.assertIn(platform.machine(), message)
        self.assertIn("built for this platform", message)

    def test_a_library_without_the_exports_is_refused(self):
        # A real system library of this platform, which has none of the exports.
        if platform.system() == "Windows":
            other = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "kernel32.dll")
        elif platform.system() == "Darwin":
            # macOS keeps its system libraries in the dyld cache, not as files: use a module of this Python instead.
            import _ctypes
            other = getattr(_ctypes, "__file__", None)
        else:
            other = next(iter(sorted(glob.glob("/lib/*/libm.so.6") + glob.glob("/usr/lib/*/libm.so.6"))), None)
        if other is None:
            self.skipTest("no libm.so.6 on this image")
        with self.assertRaises(NativeLibraryError) as refused:
            abi.load(other)
        self.assertIn("not an AgentMemory native library", str(refused.exception))

    def test_an_installed_wheel_carries_a_manifest_that_matches_its_library(self):
        info = manifest()
        if info is None:
            self.skipTest("library mode: no wheel installed")
        packaged = abi.resolve_library_path(None) if not os.environ.get("AGENTMEMORY_NATIVE_LIBRARY") else None
        if packaged is None:
            self.skipTest("an explicit library path is set")
        self.assertEqual(info["abi"], {"major": abi.ABI_MAJOR, "minor": abi.ABI_MINOR})
        family = {"linux": "manylinux_", "darwin": "macosx_", "win32": "win_"}[sys.platform]
        self.assertTrue(info["platformTag"].startswith(family), info["platformTag"])
        self.assertEqual(info["library"]["sha256"], hashlib.sha256(packaged.read_bytes()).hexdigest())
        licences = {d["id"]: d["license"] for d in info["dependencies"]}
        self.assertEqual(licences.get("Neo4j.Driver"), "Apache-2.0")
        self.assertRegex(info["build"]["commit"], r"^[0-9a-f]{40}$")
        files = {str(f) for f in importlib.metadata.files("agentmemory-native") or []}
        self.assertTrue(any(f.endswith("licenses/LICENSE") for f in files), files)
        self.assertTrue(any(f.endswith("licenses/THIRD-PARTY-NOTICES.txt") for f in files), files)
        with tempfile.TemporaryDirectory() as folder:
            damaged = Path(folder, packaged.name)
            data = bytearray(packaged.read_bytes())
            data[len(data) // 2] ^= 0xFF
            damaged.write_bytes(bytes(data))
            with self.assertRaises(NativeLibraryError) as refused:
                abi.verify_against_manifest(damaged, info)
            self.assertIn("reinstall agentmemory-native", str(refused.exception))

    def test_freeing_null_is_a_no_op(self):
        abi.load().am_v1_buffer_free(None)

    def test_invalid_handles_are_refused_not_crashing(self):
        lib = abi.load()
        flags = ctypes.c_uint32()
        values = [ctypes.c_uint64() for _ in range(4)]
        self.assertEqual(lib.am_v1_state(12345, *(ctypes.byref(v) for v in values), ctypes.byref(flags)), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_close(12345), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_cancel(12345, 1), abi.INVALID_HANDLE)

    def test_handles_round_trip_as_64_bit_integers(self):
        """Handles are opaque 64-bit values. A binding must carry all 64 bits: a JavaScript Number keeps only 53."""
        with MemoryClient(opened()) as memory:
            self.assertTrue(0 < memory.handle < 2 ** 64)
            status, handle = submit_raw(memory, b'{"schemaVersion":1,"operation":"capabilities_get"}')
            self.assertEqual(status, abi.OK)
            self.assertTrue(0 < handle < 2 ** 64)
            from agentmemory_native.client import Job
            with Job(memory, handle) as job:
                self.assertEqual(job.wait(10)["status"], "ok")
            print(f"\nMETRIC context_handle_bits={memory.handle.bit_length()} job_handle_bits={handle.bit_length()}", flush=True)


class Test2Open(unittest.TestCase):
    def test_capabilities_and_state(self):
        with MemoryClient(opened()) as memory:
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
        first = MemoryClient(opened())
        try:
            with self.assertRaises(OpenError) as refused:
                MemoryClient(opened(owner=OWNER_B))
            self.assertEqual(refused.exception.status, abi.BUSY)
            self.assertEqual(refused.exception.code, "authority_held")
        finally:
            first.close()
        MemoryClient(opened()).close()

    def test_open_close_open_repeatedly(self):
        for _ in range(5):
            with MemoryClient(opened()) as memory:
                if TIER_A:
                    self.assertEqual(memory.capabilities()["status"], "ok")
                else:
                    self.assertTrue(memory.get_context(session_id="loop", query_vector=NEAR_AXIS_0, min_score=0.0).ok)

    def test_bad_configuration_is_refused_with_a_reason(self):
        broken = opened()
        broken["store"]["surprise"] = 1
        with self.assertRaises(OpenError) as refused:
            MemoryClient(broken)
        self.assertEqual(refused.exception.status, abi.INVALID_ARGUMENT)
        self.assertEqual(refused.exception.code, "unknown_property")


@needs_store
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

    def test_extra_sections_are_off_unless_asked_and_answer_when_asked(self):
        """The session's messages, entities, relationships and time windows: an existing caller's recall is unchanged.
        Person and event details are refused: they need identity-evidence storage (a recall with text failed on it)."""
        with MemoryClient(config()) as memory:
            plain = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0)
            asked = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, min_score=0.0, max_messages=5,
                                       max_entities=10, max_relationships=10, time_windows=True)
            base = {"schemaVersion": 1, "operation": "memory_get_context", "sessionId": "s", "queryVector": NEAR_AXIS_0}
            too_many = memory.call(dict(base, maxMessages=21))
            not_bool = memory.call(dict(base, timeWindows="yes"))
            details = memory.call(dict(base, details=True))
        for name in ("windowFacts", "messages", "entities", "relationships"):
            self.assertEqual(plain.raw[name], (), name)
        self.assertEqual(asked.status, "ok", asked.raw)
        self.assertEqual(ids(asked), ids(plain), "the facts and preferences are the same with the sections on")
        self.assertEqual((too_many["status"], too_many["error"]["code"]), ("invalid", "invalid_value"))
        self.assertEqual((not_bool["status"], not_bool["error"]["code"]), ("invalid", "invalid_value"))
        self.assertEqual((details["status"], details["error"]["code"]), ("invalid", "unknown_property"))


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
        with MemoryClient(opened()) as memory:
            for data, code in cases.items():
                with self.subTest(code=code, data=data[:40]):
                    result = result_of(memory, data)
                    self.assertEqual(result["status"], "invalid")
                    self.assertEqual(result["error"]["code"], code)

    def test_what_cannot_become_a_job_is_refused_at_submit(self):
        with MemoryClient(opened()) as memory:
            self.assertEqual(submit_raw(memory, b"")[0], abi.INVALID_ARGUMENT)
            self.assertEqual(submit_raw(memory, b" " * (64 * 1024 + 1))[0], abi.INVALID_ARGUMENT)


class Test5Lifecycle(unittest.TestCase):
    @needs_store
    def test_timeout_retires_the_result(self):
        with MemoryClient(config()) as memory:
            result = memory.get_context(session_id="s", query_vector=NEAR_AXIS_0, timeout_ms=1)
        self.assertEqual((result.status, result.error.code), ("timeout", "timeout"))

    @needs_store
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
        with MemoryClient(opened()) as memory:
            with memory.submit({"schemaVersion": 1, "operation": "capabilities_get"}) as job:
                first = job.wait(10)
                second = job.poll()
                self.assertEqual(first, second)

    @needs_store
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
        memory = MemoryClient(opened())
        handle = memory.handle
        memory.close()
        lib = abi.load()
        data = b'{"schemaVersion":1,"operation":"capabilities_get"}'
        job = ctypes.c_uint64()
        self.assertEqual(lib.am_v1_submit(handle, data, len(data), 0, ctypes.byref(job)), abi.INVALID_HANDLE)
        self.assertEqual(lib.am_v1_close(handle), abi.INVALID_HANDLE)

    @needs_store
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

    @needs_store
    def test_many_recalls_keep_memory_bounded(self):
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


def rss_kib() -> int:
    """This process's resident memory now, in KiB: Linux, macOS and Windows."""
    if sys.platform.startswith("linux"):
        with open("/proc/self/status", encoding="ascii") as status:
            return next(int(line.split()[1]) for line in status if line.startswith("VmRSS:"))
    if sys.platform == "darwin":
        return int(subprocess.run(["ps", "-o", "rss=", "-p", str(os.getpid())], capture_output=True, text=True,
                                  check=True).stdout)

    class Counters(ctypes.Structure):  # PROCESS_MEMORY_COUNTERS
        _fields_ = [("cb", ctypes.c_uint32), ("PageFaultCount", ctypes.c_uint32)] + [(name, ctypes.c_size_t) for name in (
            "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
            "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetCurrentProcess.restype = ctypes.c_void_p
    kernel32.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), ctypes.c_uint32]
    counters = Counters(cb=ctypes.sizeof(Counters))
    if not kernel32.K32GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
        raise ctypes.WinError(ctypes.get_last_error())
    return counters.WorkingSetSize // 1024


class LocalHTTPServer(http.server.ThreadingHTTPServer):
    """A test server on 127.0.0.1, bound without HTTPServer's reverse lookup of its own name (socket.getfqdn): that
    lookup took about 35 s on macOS runners, for each server (NATIVE-PLAT-STORE)."""

    def server_bind(self):
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]


class FakeEmbeddings(http.server.BaseHTTPRequestHandler):
    """An OpenAI-compatible /v1/embeddings endpoint with fixed answers, so the library's HTTP path is tested exactly."""

    seen: list[dict] = []

    def do_POST(self):  # noqa: N802 - the http.server naming
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        FakeEmbeddings.seen.append({"body": body, "authorization": self.headers.get("Authorization")})
        vectors = []
        for text in body["input"]:
            if text == "wrong dimensions":
                vectors.append([1.0, 0.0, 0.0])
            elif "work" in text:
                vectors.append(NEAR_AXIS_0)
            else:
                vectors.append([0, 0, 1.0, 0, 0, 0, 0, 0])
        answer = json.dumps({"object": "list", "model": body["model"], "usage": {"prompt_tokens": 1},
                             "data": [{"object": "embedding", "index": i, "embedding": v} for i, v in enumerate(vectors)]})
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(answer.encode())

    def log_message(self, *_):
        pass


class Test6Embeddings(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = LocalHTTPServer(("127.0.0.1", 0), FakeEmbeddings)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.url = f"http://127.0.0.1:{cls.server.server_address[1]}/v1/embeddings"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def endpoint_config(self, url=None, timeout_ms=2000):
        document = opened()
        document["embedding"]["endpoint"] = {"url": url or self.url, "apiKey": "gate-key", "timeoutMs": timeout_ms}
        return document

    @needs_store
    def test_query_text_is_embedded_through_the_endpoint(self):
        FakeEmbeddings.seen.clear()
        with MemoryClient(self.endpoint_config()) as memory:
            self.assertTrue(memory.capabilities()["embedding"]["generatesEmbeddings"])
            work = memory.get_context(session_id="s", query="where do I work?", min_score=0.0)
            # Neo4j vector scores are (1 + cosine) / 2: an unrelated (orthogonal) item scores exactly 0.5.
            style = memory.get_context(session_id="s", query="how should answers sound?", min_score=0.75)
        self.assertEqual(work.status, "ok")
        self.assertEqual(work.facts[0].id, "gate-fact-a-work")
        self.assertEqual(ids(style), {"gate-pref-a-concise"})
        self.assertEqual(FakeEmbeddings.seen[0]["body"]["model"], "gate-unit-axes")
        self.assertEqual(FakeEmbeddings.seen[0]["body"]["input"], ["where do I work?"])
        self.assertEqual(FakeEmbeddings.seen[0]["authorization"], "Bearer gate-key")
        self.assertNotIn("gate-key", json.dumps(work.raw if isinstance(work.raw, dict) else dict(work.raw), default=str))

    @needs_store
    def test_a_relation_the_question_names_reaches_that_persons_own_facts(self):
        # SPEECH-RELATION-RECALL: the question finds "Anna | sister of | user" by similarity; with maxConnectionFacts,
        # Anna's own fact comes too, though it shares neither a word nor a direction with the question.
        document = self.endpoint_config()
        document["owner"] = {"mode": "per_request"}
        with MemoryClient(document) as memory:
            plain = memory.get_context(session_id="c", owner=OWNER_C, query="Where does my sister live?", min_score=0.7)
            hop = memory.get_context(session_id="c", owner=OWNER_C, query="Where does my sister live?", min_score=0.7,
                                     max_connection_facts=4)
        self.assertEqual(ids(plain), {"gate-fact-c-sister"}, plain.raw)
        self.assertEqual(ids(hop), {"gate-fact-c-sister", "gate-fact-c-city"}, hop.raw)

    def test_vectors_of_the_wrong_size_are_refused(self):
        with MemoryClient(self.endpoint_config()) as memory:
            result = memory.get_context(session_id="s", query="wrong dimensions")
        self.assertEqual((result.status, result.error.code), ("error", "embedding_mismatch"))

    def test_an_unreachable_endpoint_reports_unavailable(self):
        # Windows retries a refused local connection for about two seconds before it gives up, so allow longer than that.
        with MemoryClient(self.endpoint_config(url="http://127.0.0.1:9/v1/embeddings", timeout_ms=10000)) as memory:
            result = memory.get_context(session_id="s", query="where do I work?", timeout_ms=15000)
        self.assertEqual((result.status, result.error.code), ("unavailable", "embedding_unavailable"))

    def test_text_without_an_endpoint_and_both_inputs_are_refused(self):
        with MemoryClient(opened()) as memory:
            text_only = memory.get_context(session_id="s", query="where do I work?")
            both = memory.get_context(session_id="s", query="x", query_vector=NEAR_AXIS_0)
            neither = memory.get_context(session_id="s")
        self.assertEqual((text_only.status, text_only.error.code), ("invalid", "embedding_required"))
        self.assertEqual((both.status, both.error.code), ("invalid", "query_required"))
        self.assertEqual((neither.status, neither.error.code), ("invalid", "query_required"))


class Test7Tls(unittest.TestCase):
    """bolt+s://: TLS with the certificate checked against the system's trusted roots and the host name."""

    @needs_tls
    def test_recall_over_tls_with_the_certificate_validated(self):
        # "localhost" is tried as ::1 first, and the store listens on 127.0.0.1 only: Windows retries the refused
        # connection for about 2 s before it falls back, longer than a job's default time.
        with MemoryClient(config(uri=TLS_URI)) as memory:
            started = time.perf_counter()
            result = memory.get_context(session_id="tls", query_vector=NEAR_AXIS_0, min_score=0.0, timeout_ms=15000)
            print(f"\nMETRIC tls_first_recall_ms={(time.perf_counter() - started) * 1000:.0f}", flush=True)
        self.assertTrue(result.ok, result.raw)
        self.assertEqual(ids(result), A_IDS)

    @needs_tls
    def test_a_certificate_for_another_name_is_refused_at_open(self):
        with self.assertRaises(OpenError) as refused:
            MemoryClient(config(uri=TLS_OTHER_NAME_URI))
        self.assertEqual(refused.exception.code, "store_tls", str(refused.exception))
        print(f"\nMETRIC tls_refusal={str(refused.exception)!r}", flush=True)


class Test8ReviewFindings(unittest.TestCase):
    """The G-AOT review's findings B7-B15, each tested at its trigger."""

    @needs_store
    def test_b7_cancelling_async_calls_at_any_point_leaks_no_job(self):
        """Leaked handles stay in the job table forever, and past 257 of them every submit is refused. A cancelled
        job instead holds its place only until its work ends, so capacity must come back within seconds."""
        async def scenario():
            async with await AsyncMemoryClient.open(config(limits={"maxRunningJobs": 1, "maxPendingJobs": 1})) as memory:
                for i in range(300):
                    task = asyncio.create_task(memory.get_context(session_id="c", query_vector=NEAR_AXIS_0, timeout_ms=5000))
                    for _ in range(i % 4):
                        await asyncio.sleep(0)
                    task.cancel()
                    try:
                        await task
                    except asyncio.CancelledError:
                        pass
                    except NativeCallError as busy:
                        self.assertEqual(busy.status, abi.OVERLOADED, "only backpressure may refuse a submit here")
                for _ in range(100):
                    try:
                        return await memory.get_context(session_id="after", query_vector=NEAR_AXIS_0, min_score=0.0)
                    except NativeCallError as busy:
                        self.assertEqual(busy.status, abi.OVERLOADED)
                        await asyncio.sleep(0.1)
                raise AssertionError("capacity never came back: jobs leaked")

        self.assertEqual(ids(asyncio.run(scenario())), A_IDS, "300 cancelled calls must leave the context usable")

    @needs_store
    def test_b8_a_cancelled_open_does_not_keep_the_store(self):
        async def scenario() -> int:
            cancelled = 0
            for i in range(30):
                try:
                    opened = await asyncio.wait_for(AsyncMemoryClient.open(config()), timeout=0.0005 * (i % 6))
                except (asyncio.TimeoutError, TimeoutError):
                    cancelled += 1
                except OpenError as busy:
                    self.assertEqual(busy.code, "authority_held", "a cancelled open is still being closed")
                else:
                    # A store on the same machine can answer inside the deadline: that open is not the case under
                    # test, and it is closed here, or it would hold the store for the rest of the run.
                    await opened.close()
            await asyncio.sleep(1.0)
            return cancelled

        self.assertGreater(asyncio.run(scenario()), 0, "no open was cancelled")
        for _ in range(50):
            try:
                MemoryClient(config()).close()
                return
            except OpenError as busy:
                self.assertEqual(busy.code, "authority_held")
                time.sleep(0.1)
        raise AssertionError("the store stayed held: a cancelled open was never closed")

    @needs_store
    def test_b11_a_null_default_timeout_is_treated_as_absent(self):
        with MemoryClient(config(limits={"defaultTimeoutMs": None})) as memory:
            self.assertTrue(memory.get_context(session_id="s", query_vector=NEAR_AXIS_0).ok)

    def test_b12_invalid_text_inside_strings_is_malformed_json(self):
        with MemoryClient(opened()) as memory:
            for data in (b'{"schemaVersion":1,"operation":"capabilities_get","requestId":"\xff\xfe"}',
                         b'{"schemaVersion":1,"operation":"capabilities_get","requestId":"\\ud800"}',
                         b'{"schemaVersion":1,"operation":"capabilities_get","\\udc00":1}'):
                with self.subTest(data=data):
                    result = result_of(memory, data)
                    self.assertEqual((result["status"], result["error"]["code"]), ("invalid", "malformed_json"))
        broken = json.dumps(opened()).replace(os.environ["AMP_PASSWORD"], "\\ud800").encode()
        lib = abi.load()
        context = ctypes.c_uint64()
        error, length = ctypes.c_void_p(), ctypes.c_size_t()
        status = lib.am_v1_open(abi.ABI_VERSION, broken, len(broken), ctypes.byref(context), ctypes.byref(error), ctypes.byref(length))
        document = json.loads(abi.take_buffer(lib, error, length))
        self.assertEqual((status, document["error"]["code"]), (abi.INVALID_ARGUMENT, "malformed_json"))

    def test_b13_invalid_results_echo_the_request_id_and_operation(self):
        with MemoryClient(opened()) as memory:
            result = result_of(memory, json.dumps({"schemaVersion": 1, "operation": "memory_get_context", "requestId": "r-13",
                                                   "sessionId": "s", "queryVector": [1, 2]}).encode())
        self.assertEqual((result["status"], result["requestId"], result["operation"]), ("invalid", "r-13", "memory_get_context"))

    def test_b14_configurations_the_store_refuses_fail_at_open_as_invalid(self):
        blank_user = config(verify=False)
        blank_user["store"]["username"] = "   "
        http_uri = config(uri="http://neo4j:7474", verify=False)
        for document in (blank_user, http_uri):
            with self.subTest(store=document["store"]["uri"]):
                with self.assertRaises(OpenError) as refused:
                    MemoryClient(document)
                self.assertEqual((refused.exception.status, refused.exception.code), (abi.INVALID_ARGUMENT, "invalid_value"))

    def test_b15_state_with_a_null_output_still_clears_the_others(self):
        with MemoryClient(opened()) as memory:
            values = [ctypes.c_uint64(0xDEAD) for _ in range(4)]
            status = memory._lib.am_v1_state(memory.handle, *(ctypes.byref(v) for v in values), None)
        self.assertEqual(status, abi.INVALID_ARGUMENT)
        self.assertEqual([v.value for v in values], [0, 0, 0, 0])


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

    @needs_store
    def test_wrong_password_is_reported_as_authentication(self):
        with self.assertRaises(OpenError) as refused:
            MemoryClient(config(password="not-the-password"))
        self.assertEqual(refused.exception.code, "authentication")
        self.assertNotIn("not-the-password", str(refused.exception))


if __name__ == "__main__":
    unittest.main(verbosity=2)
