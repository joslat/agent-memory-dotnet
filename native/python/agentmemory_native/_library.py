"""Loads the native library and declares every export's exact C signature (agentmemory_native.h, ABI 1.0)."""
from __future__ import annotations

import ctypes
import os
import sys
import threading
from pathlib import Path

ABI_MAJOR = 1
ABI_MINOR = 0
ABI_VERSION = (ABI_MAJOR << 16) | ABI_MINOR

OK = 0
INVALID_ARGUMENT = 1
UNSUPPORTED_ABI = 2
INVALID_HANDLE = 3
OVERLOADED = 4
CLOSING = 5
INTERNAL_ERROR = 6
BUSY = 7
STATUS_NAMES = {
    OK: "OK", INVALID_ARGUMENT: "INVALID_ARGUMENT", UNSUPPORTED_ABI: "UNSUPPORTED_ABI",
    INVALID_HANDLE: "INVALID_HANDLE", OVERLOADED: "OVERLOADED", CLOSING: "CLOSING",
    INTERNAL_ERROR: "INTERNAL_ERROR", BUSY: "BUSY",
}

PENDING = 0
TERMINAL = 1

READ_ALLOWED = 1
ADMISSION_ALLOWED = 2
WRITE_QUIESCENT = 4
CLOSING_FLAG = 8
ERASE_PENDING = 16

_FILE_NAMES = {"win32": "agentmemory_native.dll", "darwin": "agentmemory_native.dylib"}

_loaded: dict[str, ctypes.CDLL] = {}
_load_lock = threading.Lock()


def library_file_name() -> str:
    return _FILE_NAMES.get(sys.platform, "agentmemory_native.so")


def resolve_library_path(explicit: str | os.PathLike[str] | None = None) -> Path:
    """The library to load: an explicit path, else AGENTMEMORY_NATIVE_LIBRARY, else the copy inside this package.

    No search through loader paths or the working directory: a library is loaded only from a known place.
    """
    candidate = explicit or os.environ.get("AGENTMEMORY_NATIVE_LIBRARY")
    path = Path(candidate) if candidate else Path(__file__).resolve().parent / "_native" / library_file_name()
    if not path.is_file():
        raise FileNotFoundError(f"AgentMemory native library not found at {path}")
    return path.resolve()


def load(explicit: str | os.PathLike[str] | None = None) -> ctypes.CDLL:
    """Loads the library once per path and declares its signatures. A loaded library is never unloaded."""
    path = str(resolve_library_path(explicit))
    with _load_lock:
        library = _loaded.get(path)
        if library is None:
            library = ctypes.CDLL(path)
            _declare(library)
            _loaded[path] = library
    return library


def _declare(lib: ctypes.CDLL) -> None:
    u8p = ctypes.c_char_p
    size_t = ctypes.c_size_t
    ctx = ctypes.c_uint64
    job = ctypes.c_uint64
    out_buffer = ctypes.POINTER(ctypes.c_void_p)
    out_size = ctypes.POINTER(size_t)

    lib.am_v1_abi_version.argtypes = []
    lib.am_v1_abi_version.restype = ctypes.c_uint32

    lib.am_v1_open.argtypes = [ctypes.c_uint32, u8p, size_t, ctypes.POINTER(ctx), out_buffer, out_size]
    lib.am_v1_open.restype = ctypes.c_int32

    lib.am_v1_submit.argtypes = [ctx, u8p, size_t, ctypes.c_uint32, ctypes.POINTER(job)]
    lib.am_v1_submit.restype = ctypes.c_int32

    lib.am_v1_poll.argtypes = [ctx, job, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32), out_buffer, out_size]
    lib.am_v1_poll.restype = ctypes.c_int32

    lib.am_v1_cancel.argtypes = [ctx, job]
    lib.am_v1_cancel.restype = ctypes.c_int32

    lib.am_v1_job_release.argtypes = [ctx, job]
    lib.am_v1_job_release.restype = ctypes.c_int32

    lib.am_v1_buffer_free.argtypes = [ctypes.c_void_p]
    lib.am_v1_buffer_free.restype = None

    u64p = ctypes.POINTER(ctypes.c_uint64)
    lib.am_v1_state.argtypes = [ctx, u64p, u64p, u64p, u64p, ctypes.POINTER(ctypes.c_uint32)]
    lib.am_v1_state.restype = ctypes.c_int32

    lib.am_v1_close.argtypes = [ctx]
    lib.am_v1_close.restype = ctypes.c_int32


def take_buffer(lib: ctypes.CDLL, pointer: ctypes.c_void_p, length: ctypes.c_size_t) -> bytes | None:
    """Copies a library-owned buffer into Python bytes and frees it exactly once."""
    if not pointer.value:
        return None
    try:
        return ctypes.string_at(pointer.value, length.value)
    finally:
        lib.am_v1_buffer_free(pointer)
        pointer.value = None
