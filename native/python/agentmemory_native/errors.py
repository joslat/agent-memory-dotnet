"""Errors of the native binding: a refused call, or a failed open with the library's own error document."""
from __future__ import annotations

from typing import Any

from ._library import STATUS_NAMES


class NativeCallError(RuntimeError):
    """A C ABI call returned a status other than OK. This describes the call, not an operation's outcome."""

    def __init__(self, function: str, status: int, detail: str = "") -> None:
        self.function = function
        self.status = status
        self.status_name = STATUS_NAMES.get(status, f"UNKNOWN({status})")
        super().__init__(f"{function} returned {self.status_name}" + (f": {detail}" if detail else ""))


class OpenError(NativeCallError):
    """am_v1_open refused or failed; ``code`` and ``message`` come from the library's error document."""

    def __init__(self, status: int, document: dict[str, Any] | None) -> None:
        error = (document or {}).get("error") or {}
        self.code: str = error.get("code", "unknown")
        self.message: str = error.get("message", "")
        super().__init__("am_v1_open", status, f"{self.code}: {self.message}")
