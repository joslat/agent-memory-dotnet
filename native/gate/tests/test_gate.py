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

DIMENSIONS = 8
OWNER_A = "gate-owner-a"
NEAR_AXIS_0 = [1.0, 0.1, 0, 0, 0, 0, 0, 0]
A_IDS = {"gate-fact-a-work", "gate-fact-a-colour", "gate-pref-a-concise"}
# Shared with test_langchain.py, which imports them from here.
OWNER_B = "gate-owner-b"
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


def submit_raw(client: MemoryClient, data: bytes, timeout_ms: int = 0):
    job = ctypes.c_uint64(0)
    status = client._lib.am_v1_submit(client.handle, data, len(data), timeout_ms, ctypes.byref(job))
    return status, job.value


class Test1Library(unittest.TestCase):

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


class Test5Lifecycle(unittest.TestCase):


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


class Test5Waiter(unittest.TestCase):
    """ABI 1.1, am_v1_wait: one waiter per context instead of one blocked thread per job in flight."""

    def test_closing_wakes_a_waiting_waiter(self):
        memory = MemoryClient(opened())
        seen: dict = {}

        def waiter() -> None:
            started = time.monotonic()
            seen["job"] = memory.wait_any(20_000)
            seen["seconds"] = time.monotonic() - started

        thread = threading.Thread(target=waiter)
        thread.start()
        time.sleep(0.3)
        memory.close()
        thread.join(10)
        self.assertFalse(thread.is_alive(), "the waiter was not woken by close")
        self.assertEqual(seen["job"], 0)
        self.assertLess(seen["seconds"], 5)


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


    def test_b15_state_with_a_null_output_still_clears_the_others(self):
        with MemoryClient(opened()) as memory:
            values = [ctypes.c_uint64(0xDEAD) for _ in range(4)]
            status = memory._lib.am_v1_state(memory.handle, *(ctypes.byref(v) for v in values), None)
        self.assertEqual(status, abi.INVALID_ARGUMENT)
        self.assertEqual([v.value for v in values], [0, 0, 0, 0])


# NATIVE-GATE-SHARED: the cases every binding passes (native/gate/cases), after the tests above.
from shared_cases import Test9SharedCases  # noqa: E402,F401


if __name__ == "__main__":
    unittest.main(verbosity=2)
