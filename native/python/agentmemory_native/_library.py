"""Loads the native library and declares every export's exact C signature (agentmemory_native.h, ABI 1.1)."""
from __future__ import annotations

import ctypes
import hashlib
import json
import os
import platform
import sys
import threading
from pathlib import Path
from typing import Any

ABI_MAJOR = 1
ABI_MINOR = 1
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

MANIFEST_NAME = "MANIFEST.json"


class NativeLibraryError(OSError):
    """The native library cannot be used here: built for another platform or architecture, damaged, not an AgentMemory
    library, or not the copy this package was built with. The message says which, and what to install instead."""


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


def _packaged_dir() -> Path:
    return Path(__file__).resolve().parent / "_native"


def manifest() -> dict[str, Any] | None:
    """The build manifest of the library this package carries (an installed wheel): versions, ABI, platform, the
    library's size and SHA-256, the build's commit and dependencies. None in a source checkout."""
    path = _packaged_dir() / MANIFEST_NAME
    return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else None


def verify_against_manifest(path: Path, info: dict[str, Any]) -> None:
    """Refuses a packaged library whose bytes are not the ones the manifest records."""
    expected = info["library"]
    data = path.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if len(data) != expected["size"] or digest != expected["sha256"]:
        raise NativeLibraryError(
            f"the native library at {path} is not the one this package was built with (sha256 {digest}, "
            f"expected {expected['sha256']}); reinstall agentmemory-native")


def _describe_platform() -> str:
    return f"{platform.system()} {platform.machine()}, Python {sys.version_info.major}.{sys.version_info.minor}"


def load(explicit: str | os.PathLike[str] | None = None) -> ctypes.CDLL:
    """Loads the library once per path and declares its signatures. A loaded library is never unloaded.

    The copy inside an installed wheel is checked against the wheel's manifest first. A file that cannot be loaded
    here, or that is not an AgentMemory native library, raises NativeLibraryError; nothing else is tried.
    """
    resolved = resolve_library_path(explicit)
    path = str(resolved)
    with _load_lock:
        library = _loaded.get(path)
        if library is None:
            info = manifest() if resolved.parent == _packaged_dir() else None
            if info is not None:
                verify_against_manifest(resolved, info)
            try:
                library = ctypes.CDLL(path)
            except OSError as error:
                built_for = f" This package was built for {info['platformTag']}." if info else ""
                raise NativeLibraryError(
                    f"cannot load the AgentMemory native library {path} on {_describe_platform()}: {error}.{built_for} "
                    "Install the agentmemory-native wheel built for this platform, or set AGENTMEMORY_NATIVE_LIBRARY "
                    "to a library built for it.") from error
            if not hasattr(library, "am_v1_abi_version"):
                raise NativeLibraryError(f"{path} is not an AgentMemory native library (no am_v1_abi_version export)")
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

    # ABI 1.1: one waiter per context. A 1.0 library has no such export; callers check has_wait().
    if hasattr(lib, "am_v1_wait"):
        lib.am_v1_wait.argtypes = [ctx, ctypes.c_uint32, ctypes.POINTER(job)]
        lib.am_v1_wait.restype = ctypes.c_int32


def has_wait(lib: ctypes.CDLL) -> bool:
    """Whether the library offers am_v1_wait (ABI 1.1)."""
    return hasattr(lib, "am_v1_wait")


def take_buffer(lib: ctypes.CDLL, pointer: ctypes.c_void_p, length: ctypes.c_size_t) -> bytes | None:
    """Copies a library-owned buffer into Python bytes and frees it exactly once."""
    if not pointer.value:
        return None
    try:
        return ctypes.string_at(pointer.value, length.value)
    finally:
        lib.am_v1_buffer_free(pointer)
        pointer.value = None
