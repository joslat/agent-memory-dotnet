"""Typed views of the native contract's result documents (native/CONTRACT.md)."""
from __future__ import annotations

from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, Mapping


def _frozen(value: Any) -> Any:
    """Deeply read-only metadata, so a result shared across tasks cannot be changed by one of them."""
    if isinstance(value, dict):
        return MappingProxyType({key: _frozen(item) for key, item in value.items()})
    if isinstance(value, list):
        return tuple(_frozen(item) for item in value)
    return value


@dataclass(frozen=True)
class Fact:
    id: str
    subject: str
    predicate: str
    object: str
    confidence: float
    category: str | None
    valid_from: str | None
    valid_until: str | None
    created_at: str
    metadata: Mapping[str, Any]

    @staticmethod
    def from_json(item: dict[str, Any]) -> "Fact":
        return Fact(item["id"], item["subject"], item["predicate"], item["object"], item["confidence"],
                    item.get("category"), item.get("validFrom"), item.get("validUntil"), item["createdAt"],
                    _frozen(item.get("metadata") or {}))


@dataclass(frozen=True)
class Preference:
    id: str
    category: str
    text: str
    context: str | None
    confidence: float
    created_at: str
    metadata: Mapping[str, Any]

    @staticmethod
    def from_json(item: dict[str, Any]) -> "Preference":
        return Preference(item["id"], item["category"], item["text"], item.get("context"), item["confidence"],
                          item["createdAt"], _frozen(item.get("metadata") or {}))


@dataclass(frozen=True)
class OperationError:
    code: str
    message: str


@dataclass(frozen=True)
class ContextResult:
    """The outcome of memory_get_context. ``status`` is "ok", "timeout", "cancelled", "unavailable", "invalid" or "error"."""

    status: str
    request_id: str | None
    facts: tuple[Fact, ...] = ()
    preferences: tuple[Preference, ...] = ()
    truncated: bool = False
    error: OperationError | None = None
    queued_ms: int = 0
    run_ms: int = 0
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False)

    @property
    def ok(self) -> bool:
        return self.status == "ok"

    @staticmethod
    def from_json(document: dict[str, Any]) -> "ContextResult":
        error = document.get("error")
        timing = document.get("timing") or {}
        return ContextResult(
            status=document["status"],
            request_id=document.get("requestId"),
            facts=tuple(Fact.from_json(item) for item in document.get("facts", [])),
            preferences=tuple(Preference.from_json(item) for item in document.get("preferences", [])),
            truncated=bool(document.get("truncated", False)),
            error=OperationError(error["code"], error["message"]) if error else None,
            queued_ms=int(timing.get("queuedMs", 0)),
            run_ms=int(timing.get("runMs", 0)),
            raw=_frozen(document),
        )


@dataclass(frozen=True)
class Receipt:
    """Where one operation stands: a remember, a forget or an owner erase (``kind``).

    Only a remember with ``state == "applied"`` and ``searchable`` supports saying "remembered". States: "accepted"
    (durable, not started), "running", "applied", "rejected" (nothing recall reads was written; see ``reason``),
    "partial" and "unknown" (recall stays paused until resolved), "kept" (resolved by keeping what it wrote),
    "cancelled" (forgotten or erased before it ran), "forgotten" (a remember whose memories were closed by a forget:
    no longer used, not deleted), "erased" (the owner was erased since).
    """

    operation_id: str
    source_id: str
    state: str
    duplicate: bool
    searchable: bool
    reason: str | None
    occurred_at: str
    accepted_at: str
    started_at: str | None
    finished_at: str | None
    facts: tuple[str, ...]
    preferences: tuple[str, ...]
    closed: tuple[str, ...]
    kind: str = "remember"
    target_operation_id: str | None = None
    deleted: int | None = None

    @property
    def ended(self) -> bool:
        return self.state not in ("accepted", "running")

    @property
    def remembered(self) -> bool:
        return self.state == "applied" and self.searchable

    @staticmethod
    def from_json(item: dict[str, Any]) -> "Receipt":
        return Receipt(item["operationId"], item["sourceId"], item["state"], bool(item.get("duplicate", False)),
                       bool(item.get("searchable", False)), item.get("reason"), item["occurredAt"], item["acceptedAt"],
                       item.get("startedAt"), item.get("finishedAt"), tuple(item.get("facts", ())),
                       tuple(item.get("preferences", ())), tuple(item.get("closed", ())), item.get("kind", "remember"),
                       item.get("targetOperationId"), item.get("deleted"))


@dataclass(frozen=True)
class WriteResult:
    """The outcome of memory_remember, memory_operation_get or memory_operation_resolve.

    ``status == "ok"`` means a receipt came back (read its state); any other status means the request itself was refused
    or could not be accepted, and nothing was saved by it (``error`` says why).
    """

    status: str
    request_id: str | None
    receipt: Receipt | None = None
    error: OperationError | None = None
    queued_ms: int = 0
    run_ms: int = 0
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False)

    @property
    def ok(self) -> bool:
        return self.status == "ok"

    @staticmethod
    def from_json(document: dict[str, Any]) -> "WriteResult":
        error = document.get("error")
        timing = document.get("timing") or {}
        receipt = document.get("receipt")
        return WriteResult(
            status=document["status"],
            request_id=document.get("requestId"),
            receipt=Receipt.from_json(receipt) if receipt else None,
            error=OperationError(error["code"], error["message"]) if error else None,
            queued_ms=int(timing.get("queuedMs", 0)),
            run_ms=int(timing.get("runMs", 0)),
            raw=_frozen(document),
        )


@dataclass(frozen=True)
class ContextState:
    state_revision: int
    policy_epoch: int
    deletion_epoch: int
    memory_revision: int
    flags: int

    @property
    def read_allowed(self) -> bool:
        return bool(self.flags & 1)

    @property
    def admission_allowed(self) -> bool:
        return bool(self.flags & 2)

    @property
    def write_quiescent(self) -> bool:
        return bool(self.flags & 4)

    @property
    def closing(self) -> bool:
        return bool(self.flags & 8)
