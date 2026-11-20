"""Sync and async clients over the AgentMemory native library. Framework-neutral: no speech or agent concepts here."""
from __future__ import annotations

import asyncio
import ctypes
import json
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Mapping, Sequence

from . import _library as abi
from .errors import NativeCallError, OpenError
from .results import ContextResult, ContextState

#: How long one poll waits inside the library before control returns to Python (milliseconds).
POLL_SLICE_MS = 50


def _encode(document: Mapping[str, Any]) -> bytes:
    return json.dumps(document, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")


class Job:
    """One submitted operation. Release it when done (``with job:`` does); a released handle is never reused."""

    def __init__(self, client: "MemoryClient", handle: int) -> None:
        self._client = client
        self.handle = handle
        self._released = False
        self._lock = threading.Lock()

    def poll(self, wait_ms: int = 0) -> dict[str, Any] | None:
        """The result document once the job has one, else None. Waits at most ``wait_ms`` inside the library."""
        lib = self._client._lib
        disposition = ctypes.c_uint32(abi.PENDING)
        pointer = ctypes.c_void_p()
        length = ctypes.c_size_t(0)
        status = lib.am_v1_poll(self._client._context, self.handle, max(0, int(wait_ms)), ctypes.byref(disposition),
                                ctypes.byref(pointer), ctypes.byref(length))
        data = abi.take_buffer(lib, pointer, length)
        if status != abi.OK:
            raise NativeCallError("am_v1_poll", status)
        if disposition.value != abi.TERMINAL or data is None:
            return None
        return json.loads(data.decode("utf-8"))

    def cancel(self) -> None:
        """Asks the job to stop. Its result then says "cancelled" unless it had already finished."""
        status = self._client._lib.am_v1_cancel(self._client._context, self.handle)
        if status not in (abi.OK, abi.INVALID_HANDLE):
            raise NativeCallError("am_v1_cancel", status)

    def release(self) -> None:
        """Gives up this handle. Idempotent. Unfinished work stays the library's to finish."""
        with self._lock:
            if self._released:
                return
            self._released = True
        status = self._client._lib.am_v1_job_release(self._client._context, self.handle)
        if status not in (abi.OK, abi.INVALID_HANDLE):
            raise NativeCallError("am_v1_job_release", status)

    def wait(self, timeout_s: float) -> dict[str, Any]:
        """Polls until the result arrives. The library's own deadline normally ends the job first."""
        deadline = time.monotonic() + timeout_s
        while True:
            result = self.poll(POLL_SLICE_MS)
            if result is not None:
                return result
            if time.monotonic() > deadline:
                self.cancel()
                raise TimeoutError(f"job {self.handle:#x} delivered no result within {timeout_s:.1f} s")

    def __enter__(self) -> "Job":
        return self

    def __exit__(self, *_: object) -> None:
        self.release()


class MemoryClient:
    """A logical context of the native library: open on construction, close when done (``with`` closes)."""

    def __init__(self, config: Mapping[str, Any], *, library: str | None = None) -> None:
        self._lib = abi.load(library)
        version = self._lib.am_v1_abi_version()
        if version >> 16 != abi.ABI_MAJOR:
            raise NativeCallError("am_v1_abi_version", abi.UNSUPPORTED_ABI,
                                  f"library ABI {version >> 16}.{version & 0xFFFF}, binding ABI {abi.ABI_MAJOR}.{abi.ABI_MINOR}")
        data = _encode(config)
        context = ctypes.c_uint64(0)
        pointer = ctypes.c_void_p()
        length = ctypes.c_size_t(0)
        status = self._lib.am_v1_open(abi.ABI_VERSION, data, len(data), ctypes.byref(context), ctypes.byref(pointer),
                                      ctypes.byref(length))
        error = abi.take_buffer(self._lib, pointer, length)
        if status != abi.OK:
            raise OpenError(status, json.loads(error.decode("utf-8")) if error else None)
        self._context = context.value
        self._closed = False
        self.default_timeout_ms = int((config.get("limits") or {}).get("defaultTimeoutMs", 2000))

    # Low level -----------------------------------------------------------------------------------------------------
    def submit(self, request: Mapping[str, Any], timeout_ms: int = 0) -> Job:
        """Schedules one operation; raises NativeCallError when it is not admitted (OVERLOADED, CLOSING, ...)."""
        data = _encode(request)
        job = ctypes.c_uint64(0)
        status = self._lib.am_v1_submit(self._context, data, len(data), max(0, int(timeout_ms)), ctypes.byref(job))
        if status != abi.OK:
            raise NativeCallError("am_v1_submit", status)
        return Job(self, job.value)

    def call(self, request: Mapping[str, Any], timeout_ms: int = 0) -> dict[str, Any]:
        """Submits, waits for the result document, and releases the job."""
        budget_ms = timeout_ms or self.default_timeout_ms
        with self.submit(request, timeout_ms) as job:
            return job.wait(budget_ms / 1000 + 5)

    def state(self) -> ContextState:
        values = [ctypes.c_uint64(0) for _ in range(4)]
        flags = ctypes.c_uint32(0)
        status = self._lib.am_v1_state(self._context, *(ctypes.byref(v) for v in values), ctypes.byref(flags))
        if status != abi.OK:
            raise NativeCallError("am_v1_state", status)
        return ContextState(*(v.value for v in values), flags.value)

    # Operations ----------------------------------------------------------------------------------------------------
    def capabilities(self) -> dict[str, Any]:
        return self.call({"schemaVersion": 1, "operation": "capabilities_get"})

    def get_context(self, *, session_id: str, query: str | None = None, query_vector: Sequence[float] | None = None,
                    owner: str | None = None,
                    max_facts: int = 5, max_preferences: int = 5, min_score: float | None = None,
                    request_id: str | None = None, timeout_ms: int = 0) -> ContextResult:
        """Owner-scoped recall of facts and preferences.

        Pass ``query`` (text, embedded by the context's embedding endpoint) or ``query_vector`` (made by the caller).
        """
        return ContextResult.from_json(self.call(
            get_context_request(session_id=session_id, query=query, query_vector=query_vector, owner=owner,
                                max_facts=max_facts,
                                max_preferences=max_preferences, min_score=min_score, request_id=request_id),
            timeout_ms))

    # Lifetime ------------------------------------------------------------------------------------------------------
    def close(self, timeout_s: float = 10.0) -> None:
        """Stops admission and waits until running work has settled, then disposes the context."""
        if self._closed:
            return
        deadline = time.monotonic() + timeout_s
        while True:
            status = self._lib.am_v1_close(self._context)
            if status in (abi.OK, abi.INVALID_HANDLE):
                self._closed = True
                return
            if status != abi.BUSY:
                raise NativeCallError("am_v1_close", status)
            if time.monotonic() > deadline:
                raise NativeCallError("am_v1_close", status, f"work still running after {timeout_s:.1f} s")
            time.sleep(0.01)

    @property
    def handle(self) -> int:
        return self._context

    def __enter__(self) -> "MemoryClient":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def get_context_request(*, session_id: str, query: str | None = None, query_vector: Sequence[float] | None = None,
                        owner: str | None = None,
                        max_facts: int = 5, max_preferences: int = 5, min_score: float | None = None,
                        request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {
        "schemaVersion": 1,
        "operation": "memory_get_context",
        "sessionId": session_id,
        "maxFacts": max_facts,
        "maxPreferences": max_preferences,
    }
    if query is not None:
        request["query"] = query
    if query_vector is not None:
        request["queryVector"] = [float(v) for v in query_vector]
    if owner is not None:
        request["owner"] = owner
    if min_score is not None:
        request["minScore"] = min_score
    if request_id is not None:
        request["requestId"] = request_id
    return request


class AsyncMemoryClient:
    """The same operations for asyncio. Every native call runs on a small bounded thread pool, never on the loop.

    Cancelling an awaiting task cancels the native job; its capacity is returned when its work has really ended.
    """

    def __init__(self, client: MemoryClient, *, bridge_threads: int = 4) -> None:
        self._client = client
        self._bridge = ThreadPoolExecutor(max_workers=bridge_threads, thread_name_prefix="agentmemory-native")

    @classmethod
    async def open(cls, config: Mapping[str, Any], *, library: str | None = None, bridge_threads: int = 4) -> "AsyncMemoryClient":
        loop = asyncio.get_running_loop()
        bridge = ThreadPoolExecutor(max_workers=1, thread_name_prefix="agentmemory-native-open")
        try:
            client = await loop.run_in_executor(bridge, lambda: MemoryClient(config, library=library))
        finally:
            bridge.shutdown(wait=False)
        return cls(client, bridge_threads=bridge_threads)

    @property
    def sync(self) -> MemoryClient:
        return self._client

    async def call(self, request: Mapping[str, Any], timeout_ms: int = 0) -> dict[str, Any]:
        loop = asyncio.get_running_loop()
        job = await loop.run_in_executor(self._bridge, self._client.submit, request, timeout_ms)
        try:
            while True:
                result = await loop.run_in_executor(self._bridge, job.poll, POLL_SLICE_MS)
                if result is not None:
                    return result
        except asyncio.CancelledError:
            self._bridge.submit(job.cancel)
            raise
        finally:
            self._bridge.submit(job.release)

    async def capabilities(self) -> dict[str, Any]:
        return await self.call({"schemaVersion": 1, "operation": "capabilities_get"})

    async def get_context(self, **arguments: Any) -> ContextResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return ContextResult.from_json(await self.call(get_context_request(**arguments), timeout_ms))

    async def state(self) -> ContextState:
        return await asyncio.get_running_loop().run_in_executor(self._bridge, self._client.state)

    async def close(self, timeout_s: float = 10.0) -> None:
        loop = asyncio.get_running_loop()
        try:
            await loop.run_in_executor(self._bridge, self._client.close, timeout_s)
        finally:
            self._bridge.shutdown(wait=True)

    async def __aenter__(self) -> "AsyncMemoryClient":
        return self

    async def __aexit__(self, *_: object) -> None:
        await self.close()
