"""Sync and async clients over the AgentMemory native library. Framework-neutral: no speech or agent concepts here."""
from __future__ import annotations

import asyncio
import ctypes
import json
import threading
import time
from concurrent.futures import Future, ThreadPoolExecutor
from typing import Any, Mapping, Sequence

from . import _library as abi
from .errors import NativeCallError, OpenError
from .results import ContextResult, ContextState, WriteResult

#: How long one poll waits inside the library before control returns to Python (milliseconds).
POLL_SLICE_MS = 50


def _encode(document: Mapping[str, Any]) -> bytes:
    return json.dumps(document, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")


class JobStopped(Exception):
    """The caller stopped waiting before the job was submitted; nothing ran."""


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
        limits = config.get("limits") or {}
        # The library reads a JSON null as "absent"; so does this, before anything is opened.
        self.default_timeout_ms = int(limits.get("defaultTimeoutMs") or 2000)
        self.max_pending_jobs = int(limits.get("maxPendingJobs") or 32)
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
        return self.run_job(request, timeout_ms)

    def run_job(self, request: Mapping[str, Any], timeout_ms: int = 0,
                stop: threading.Event | None = None) -> dict[str, Any]:
        """One job from submit to release, in the calling thread.

        When ``stop`` is set, the job is cancelled first and released after its result arrives, in that order, here:
        the handle never leaves this function, so nothing can drop it.
        """
        if stop is not None and stop.is_set():
            raise JobStopped("stopped before it was submitted")
        budget_s = (timeout_ms or self.default_timeout_ms) / 1000 + 5
        job = self.submit(request, timeout_ms)
        try:
            deadline = time.monotonic() + budget_s
            cancelled = False
            while True:
                if stop is not None and stop.is_set() and not cancelled:
                    job.cancel()
                    cancelled = True
                result = job.poll(POLL_SLICE_MS)
                if result is not None:
                    return result
                if time.monotonic() > deadline and not cancelled:
                    job.cancel()
                    cancelled = True
                    deadline = time.monotonic() + 5
                elif time.monotonic() > deadline:
                    raise TimeoutError(f"job {job.handle:#x} delivered no result, even after a cancel")
        finally:
            job.release()

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

    def schema_status(self, timeout_ms: int = 0) -> dict[str, Any]:
        """Admin profile: what the store holds of AgentMemory's schema (complete, missing objects, vector index
        dimensions). Changes nothing."""
        return self.call({"schemaVersion": 1, "operation": "schema_status"}, timeout_ms)

    def schema_bootstrap(self, timeout_ms: int = 120_000) -> dict[str, Any]:
        """Admin profile: creates what is missing of the schema and applies its migrations, as the managed bootstrap
        does; what is there already is left as it is. Returns the schema as it is after."""
        return self.call({"schemaVersion": 1, "operation": "schema_bootstrap"}, timeout_ms)

    def get_context(self, *, session_id: str, query: str | None = None, query_vector: Sequence[float] | None = None,
                    owner: str | None = None,
                    max_facts: int = 5, max_preferences: int = 5, min_score: float | None = None,
                    request_id: str | None = None, timeout_ms: int = 0, **sections: Any) -> ContextResult:
        """Owner-scoped recall of facts and preferences, and the sections ``get_context_request`` names (max_messages,
        max_entities, max_relationships, time_windows, max_connection_facts) when asked for.

        Pass ``query`` (text, embedded by the context's embedding endpoint) or ``query_vector`` (made by the caller).
        """
        return ContextResult.from_json(self.call(
            get_context_request(session_id=session_id, query=query, query_vector=query_vector, owner=owner,
                                max_facts=max_facts,
                                max_preferences=max_preferences, min_score=min_score, request_id=request_id,
                                **sections),
            timeout_ms))

    def remember(self, *, source_id: str, session_id: str, text: str, occurred_at: str, origin: str | None = None,
                 request_id: str | None = None, timeout_ms: int = 0) -> WriteResult:
        """Remembers the owner's own words (``explicit_write`` or ``learning``), through the durable journal.

        ``source_id`` is the host's stable id for this spoken item and revision: sending it again returns the same
        receipt and runs nothing. ``occurred_at`` is when the words were said, ISO 8601 with an offset. The call waits
        until the write ends or ``timeout_ms`` passes; either way an accepted write comes back as a receipt. ``origin``
        is "explicit" (the owner asked; the default) or, with the ``learning`` profile, "observed" (admitted unasked).
        """
        return WriteResult.from_json(self.call(
            remember_request(source_id=source_id, session_id=session_id, text=text, occurred_at=occurred_at,
                             origin=origin, request_id=request_id), timeout_ms))

    def operation(self, *, operation_id: str | None = None, source_id: str | None = None,
                  request_id: str | None = None, timeout_ms: int = 0) -> WriteResult:
        """The receipt of one write, by its operation id or its source id."""
        return WriteResult.from_json(self.call(
            operation_get_request(operation_id=operation_id, source_id=source_id, request_id=request_id), timeout_ms))

    def resolve(self, *, operation_id: str, request_id: str | None = None, timeout_ms: int = 0) -> WriteResult:
        """Ends a partial or unknown write by keeping what it wrote, as it is; recall resumes when nothing else is open."""
        return WriteResult.from_json(self.call(
            operation_resolve_request(operation_id=operation_id, request_id=request_id), timeout_ms))

    def forget(self, *, operation_id: str | None = None, memory_ids: Sequence[str] | None = None,
               request_id: str | None = None, timeout_ms: int = 0) -> WriteResult:
        """Stops using what one remember produced (``operation_id``) or the owner's memories by id (``memory_ids``).

        Forgotten memories are closed, not deleted: recall no longer returns them. A remember that has not started yet is
        cancelled instead.
        """
        return WriteResult.from_json(self.call(
            forget_request(operation_id=operation_id, memory_ids=memory_ids, request_id=request_id), timeout_ms))

    def erase_owner(self, *, confirm: bool, request_id: str | None = None, timeout_ms: int = 0) -> WriteResult:
        """Deletes everything of the configured owner and purges their words from the journal. Cannot be undone.

        Refused while any write is accepted or running. The receipt says ``applied`` only when nothing of the owner is
        left in the store.
        """
        return WriteResult.from_json(self.call(erase_owner_request(confirm=confirm, request_id=request_id), timeout_ms))

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
                        request_id: str | None = None, max_messages: int = 0, max_entities: int = 0,
                        max_relationships: int = 0, time_windows: bool = False,
                        max_connection_facts: int = 0) -> dict[str, Any]:
    """A recall request. The extra sections (the session's own earlier messages, entities, relationships, the facts of
    a time the question names) are sent only when asked for, so the request stays the one an older library reads."""
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
    for key, value in (("maxMessages", max_messages), ("maxEntities", max_entities),
                       ("maxRelationships", max_relationships), ("timeWindows", time_windows),
                       ("maxConnectionFacts", max_connection_facts)):
        if value:
            request[key] = value
    return request


def remember_request(*, source_id: str, session_id: str, text: str, occurred_at: str, origin: str | None = None,
                     request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {"schemaVersion": 1, "operation": "memory_remember", "sourceId": source_id,
                               "sessionId": session_id, "text": text, "occurredAt": occurred_at}
    if origin is not None:
        request["origin"] = origin
    if request_id is not None:
        request["requestId"] = request_id
    return request


def operation_get_request(*, operation_id: str | None = None, source_id: str | None = None,
                          request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {"schemaVersion": 1, "operation": "memory_operation_get"}
    if operation_id is not None:
        request["operationId"] = operation_id
    if source_id is not None:
        request["sourceId"] = source_id
    if request_id is not None:
        request["requestId"] = request_id
    return request


def operation_resolve_request(*, operation_id: str, request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {"schemaVersion": 1, "operation": "memory_operation_resolve",
                               "operationId": operation_id, "resolution": "keep"}
    if request_id is not None:
        request["requestId"] = request_id
    return request


def forget_request(*, operation_id: str | None = None, memory_ids: Sequence[str] | None = None,
                   request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {"schemaVersion": 1, "operation": "memory_forget"}
    if operation_id is not None:
        request["operationId"] = operation_id
    if memory_ids is not None:
        request["memoryIds"] = list(memory_ids)
    if request_id is not None:
        request["requestId"] = request_id
    return request


def erase_owner_request(*, confirm: bool, request_id: str | None = None) -> dict[str, Any]:
    request: dict[str, Any] = {"schemaVersion": 1, "operation": "memory_erase_owner", "confirm": bool(confirm)}
    if request_id is not None:
        request["requestId"] = request_id
    return request


class AsyncMemoryClient:
    """The same operations for asyncio. Native calls never run on the event loop.

    Each call runs from submit to release in one bridge thread; the pool has a thread for every job the context can
    admit, so no call waits behind another. Cancelling an awaiting task returns at once and asks that thread to cancel
    the native job and release it; the job keeps its capacity until its work has really ended.
    """

    def __init__(self, client: MemoryClient, *, bridge_threads: int | None = None) -> None:
        self._client = client
        self._bridge = ThreadPoolExecutor(max_workers=bridge_threads or client.max_pending_jobs + 2,
                                          thread_name_prefix="agentmemory-native")
        self._closed = False

    @classmethod
    async def open(cls, config: Mapping[str, Any], *, library: str | None = None,
                   bridge_threads: int | None = None) -> "AsyncMemoryClient":
        """Opens off the event loop. If the awaiting task is cancelled, a context that still opens is closed again."""
        opened: Future = Future()

        def run() -> None:
            if not opened.set_running_or_notify_cancel():
                return
            try:
                opened.set_result(MemoryClient(config, library=library))
            except BaseException as error:  # noqa: BLE001 - handed to the awaiting task
                opened.set_exception(error)

        threading.Thread(target=run, name="agentmemory-native-open", daemon=True).start()
        try:
            client = await asyncio.wrap_future(opened)
        except asyncio.CancelledError:
            def close_late(future: Future) -> None:
                if not future.cancelled() and future.exception() is None:
                    future.result().close()
            opened.add_done_callback(close_late)
            raise
        return cls(client, bridge_threads=bridge_threads)

    @property
    def sync(self) -> MemoryClient:
        return self._client

    async def call(self, request: Mapping[str, Any], timeout_ms: int = 0) -> dict[str, Any]:
        if self._closed:
            raise RuntimeError("the client is closed")
        stop = threading.Event()
        running = self._bridge.submit(self._client.run_job, request, timeout_ms, stop)
        try:
            return await asyncio.wrap_future(running)
        except asyncio.CancelledError:
            stop.set()
            raise

    async def capabilities(self) -> dict[str, Any]:
        return await self.call({"schemaVersion": 1, "operation": "capabilities_get"})

    async def schema_status(self, timeout_ms: int = 0) -> dict[str, Any]:
        return await self.call({"schemaVersion": 1, "operation": "schema_status"}, timeout_ms)

    async def schema_bootstrap(self, timeout_ms: int = 120_000) -> dict[str, Any]:
        return await self.call({"schemaVersion": 1, "operation": "schema_bootstrap"}, timeout_ms)

    async def get_context(self, **arguments: Any) -> ContextResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return ContextResult.from_json(await self.call(get_context_request(**arguments), timeout_ms))

    async def remember(self, **arguments: Any) -> WriteResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return WriteResult.from_json(await self.call(remember_request(**arguments), timeout_ms))

    async def operation(self, **arguments: Any) -> WriteResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return WriteResult.from_json(await self.call(operation_get_request(**arguments), timeout_ms))

    async def resolve(self, **arguments: Any) -> WriteResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return WriteResult.from_json(await self.call(operation_resolve_request(**arguments), timeout_ms))

    async def forget(self, **arguments: Any) -> WriteResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return WriteResult.from_json(await self.call(forget_request(**arguments), timeout_ms))

    async def erase_owner(self, **arguments: Any) -> WriteResult:
        timeout_ms = int(arguments.pop("timeout_ms", 0))
        return WriteResult.from_json(await self.call(erase_owner_request(**arguments), timeout_ms))

    async def state(self) -> ContextState:
        return await asyncio.get_running_loop().run_in_executor(self._bridge, self._client.state)

    async def close(self, timeout_s: float = 10.0) -> None:
        """Closes on its own thread. The event loop never blocks on it, even when this await is cancelled."""
        if self._closed:
            return
        self._closed = True
        try:
            await asyncio.to_thread(self._client.close, timeout_s)
        finally:
            self._bridge.shutdown(wait=False)

    async def __aenter__(self) -> "AsyncMemoryClient":
        return self

    async def __aexit__(self, *_: object) -> None:
        await self.close()
