"""A LangGraph ``BaseStore`` backed by AgentMemory — demo-grade (meeting-demo-track.md D2).

PROTOTYPE. Not published, not packaged, not a preview of the SDK. The productized cross-language SDK
follows the published designs; this talks to the Spike-0 prototype host over a draft wire.

Why this adapter exists rather than a generic key-value one
-----------------------------------------------------------
LangGraph's ``BaseStore`` is a namespaced document store: ``put`` takes an arbitrary dict, ``search``
does text/semantic matching. Every backend implements roughly that.

This one maps ``put`` onto a **typed memory write** and exposes something no other ``BaseStore``
backend can offer: a ``as_of`` search filter that answers *"what did we believe about X on date D?"*.
That is not a nicety layered on top — it falls out of the engine being bitemporal underneath, and a
key-value store cannot retrofit it, because the information was never recorded.

Two Wave-C features ride along, both read at session start:

* ``working_memory(owner)`` — the compiled per-owner block. A point-read, so unlike a vector search it
  cannot be starved by a global top-K.
* ``delta(owner, since)`` — the resume brief: *"here is what changed since you were last here."*

Standard library plus ``langgraph`` only. No SDK, no generated client: this is deliberately the
smallest thing that can be demonstrated, and it is meant to be replaced.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Any, Iterable, Sequence

from langgraph.store.base import (
    BaseStore,
    GetOp,
    Item,
    ListNamespacesOp,
    Op,
    PutOp,
    Result,
    SearchItem,
    SearchOp,
)

DEFAULT_BASE_URL = "http://localhost:5173"


class AgentMemoryStoreError(RuntimeError):
    """Transport or protocol failure talking to the prototype host."""


def _utc(value: str | None) -> datetime | None:
    if not value:
        return None
    text = value.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    return datetime.fromisoformat(text).astimezone(timezone.utc)


def _stamp(value: Any) -> str | None:
    """Render a datetime (or passthrough string) as an ISO-8601 instant the host accepts."""
    if value is None:
        return None
    if isinstance(value, str):
        return value
    if isinstance(value, datetime):
        moment = value if value.tzinfo else value.replace(tzinfo=timezone.utc)
        return moment.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    raise TypeError(f"as_of must be a datetime or an ISO-8601 string, got {type(value).__name__}")


class AgentMemoryStore(BaseStore):
    """LangGraph store over AgentMemory.

    Namespace convention: the LAST namespace segment is the memory owner, so
    ``("memories", "alice")`` scopes to Alice. Owner isolation is enforced by the engine, not here —
    an adapter that filtered client-side would be a suggestion rather than a boundary.
    """

    # Only batch/abatch are abstract on BaseStore; get/search/put/delete are concrete and dispatch
    # through them. Implementing at the batch layer means the public signatures -- including
    # search(..., filter=...) -- are LangGraph's own, unmodified. An adapter that added an `as_of`
    # keyword to `search` would not be a BaseStore any more, and "works with any LangGraph agent" is
    # the whole claim.
    supports_ttl = False

    def __init__(self, base_url: str = DEFAULT_BASE_URL, *, timeout: float = 30.0) -> None:
        self._base_url = base_url.rstrip("/")
        self._timeout = timeout

    # ── transport ─────────────────────────────────────────────────────

    def _request(self, method: str, path: str, payload: dict | None = None) -> Any:
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        request = urllib.request.Request(
            self._base_url + path,
            data=data,
            headers={"Content-Type": "application/json"} if data else {},
            method=method,
        )
        try:
            with urllib.request.urlopen(request, timeout=self._timeout) as response:
                body = response.read().decode("utf-8")
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            raise AgentMemoryStoreError(
                f"{method} {path} failed: HTTP {error.code} {error.read().decode('utf-8')[:400]}"
            ) from error
        except urllib.error.URLError as error:
            raise AgentMemoryStoreError(f"{method} {path} failed: {error}") from error

    @staticmethod
    def _owner(namespace: Sequence[str]) -> str | None:
        """The owner is the last namespace segment; a bare namespace is unscoped."""
        return namespace[-1] if namespace else None

    # ── the typed mapping ─────────────────────────────────────────────

    @staticmethod
    def _to_fact(key: str, owner: str | None, value: dict[str, Any]) -> dict[str, Any]:
        """Map a LangGraph value dict onto a typed fact.

        The convention is explicit rather than inferred: ``subject``/``predicate``/``object`` make a
        triple. Anything else is rejected loudly instead of being coerced into
        ``(key, "has_value", json.dumps(value))`` -- a store that silently accepts arbitrary shapes and
        stores them as opaque strings is a key-value store wearing a memory system's name, and every
        typed capability below it stops working without saying so.

        Three optional keys are *control*, not content: ``valid_from``/``valid_until`` set the
        real-world window, ``recorded_at`` sets the transaction instant, and ``supersedes`` names the
        fact this one replaces.
        """
        missing = [f for f in ("subject", "predicate", "object") if f not in value]
        if missing:
            raise ValueError(
                f"AgentMemoryStore stores TYPED facts: value must carry "
                f"'subject', 'predicate' and 'object' (missing: {', '.join(missing)}). "
                "This adapter deliberately does not accept arbitrary documents — the bitemporal and "
                "isolation guarantees it exists to expose are defined on triples."
            )

        known = {"subject", "predicate", "object", "confidence",
                 "valid_from", "valid_until", "recorded_at", "supersedes"}
        unknown = sorted(set(value) - known)
        if unknown:
            # Dropping unknown keys silently would let a caller believe data was stored that was not,
            # and they would only find out when a later read came back short.
            raise ValueError(
                f"AgentMemoryStore does not store arbitrary fields; unknown key(s): "
                f"{', '.join(unknown)}. Express them as further triples."
            )

        return {
            "key": key,
            "ownerId": owner,
            "subject": str(value["subject"]),
            "predicate": str(value["predicate"]),
            "object": str(value["object"]),
            "confidence": value.get("confidence"),
            "validFrom": _stamp(value.get("valid_from")),
            "validUntil": _stamp(value.get("valid_until")),
            "recordedAtUtc": _stamp(value.get("recorded_at")),
            "supersedes": value.get("supersedes"),
        }

    @staticmethod
    def _from_fact(wire: dict[str, Any]) -> dict[str, Any]:
        return {
            "subject": wire["subject"],
            "predicate": wire["predicate"],
            "object": wire["object"],
            "confidence": wire["confidence"],
            "valid_from": wire.get("validFrom"),
            "valid_until": wire.get("validUntil"),
            "owner_id": wire.get("ownerId"),
        }

    # ── BaseStore ─────────────────────────────────────────────────────

    def batch(self, ops: Iterable[Op]) -> list[Result]:
        results: list[Result] = []
        for op in ops:
            if isinstance(op, PutOp):
                results.append(self._put(op))
            elif isinstance(op, GetOp):
                results.append(self._get(op))
            elif isinstance(op, SearchOp):
                results.append(self._search(op))
            elif isinstance(op, ListNamespacesOp):
                # Honest refusal. The engine namespaces by owner, not by arbitrary path, so there is
                # no faithful answer -- and returning [] would read as "no namespaces exist", which is
                # a different and false claim.
                raise NotImplementedError(
                    "AgentMemoryStore does not enumerate namespaces: the engine scopes by owner, not "
                    "by an arbitrary namespace tree, so any answer here would be invented."
                )
            else:  # pragma: no cover - defensive
                raise NotImplementedError(f"unsupported op: {type(op).__name__}")
        return results

    async def abatch(self, ops: Iterable[Op]) -> list[Result]:
        # Synchronous under an async signature, deliberately. This is a demo adapter over urllib; a
        # real one would use an async client. Pretending otherwise by wrapping in a thread would add
        # machinery that hides, rather than removes, the blocking call.
        return self.batch(ops)

    def _put(self, op: PutOp) -> None:
        owner = self._owner(op.namespace)
        if op.value is None:
            raise NotImplementedError(
                "AgentMemoryStore does not delete. Memory is invalidated, never removed — a fact is "
                "closed on the transaction clock so as-of recall can still see it, which is exactly "
                "what a delete would destroy."
            )
        self._request("POST", "/v1/facts", self._to_fact(op.key, owner, dict(op.value)))
        return None

    def _get(self, op: GetOp) -> Item | None:
        wire = self._request("GET", f"/v1/facts/{op.key}")
        if wire is None:
            return None

        # The engine's created/updated stamps are not on this draft wire, so `now` stands in. Recorded
        # here rather than quietly: a caller who trusts these timestamps would be trusting the client
        # clock, and the real contract carries the server's.
        now = datetime.now(timezone.utc)
        return Item(
            value=self._from_fact(wire),
            key=op.key,
            namespace=tuple(op.namespace),
            created_at=now,
            updated_at=now,
        )

    def _search(self, op: SearchOp) -> list[SearchItem]:
        owner = self._owner(op.namespace_prefix)
        filters = dict(op.filter or {})

        # THE BEAT OF THE DEMO. `as_of` arrives through LangGraph's own `filter` argument -- no
        # bespoke method, no changed signature -- so any agent already calling `store.search(...)`
        # gets point-in-time recall by adding one filter key.
        as_of = _stamp(filters.pop("as_of", None))
        system_as_of = _stamp(filters.pop("system_as_of", None))
        if filters:
            raise ValueError(
                f"AgentMemoryStore supports the 'as_of' and 'system_as_of' filters; "
                f"got unsupported: {', '.join(sorted(filters))}. Ignoring an unknown filter would "
                "return a broader result set than the caller asked for and look like a match."
            )

        payload = {
            "sessionId": "langgraph-demo",
            "userId": owner,
            "query": op.query or "",
            "maxFacts": op.limit,
            "asOf": as_of,
            "systemAsOf": system_as_of,
        }
        wire = self._request("POST", "/v1/recall", payload)
        now = datetime.now(timezone.utc)

        items = [
            SearchItem(
                namespace=tuple(op.namespace_prefix),
                key=fact["id"],
                value=self._from_fact(fact),
                created_at=now,
                updated_at=now,
                # No score: the engine returns ranked facts but this draft wire does not carry the
                # similarity value. None means "not scored", which is honest; a fabricated 1.0 would
                # let a caller rank on a number that means nothing.
                score=None,
            )
            for fact in wire["facts"]
        ]
        return items[op.offset :] if op.offset else items

    # ── beyond BaseStore: the two session-start reads ─────────────────

    def working_memory(self, owner: str) -> str | None:
        """The compiled per-owner block, or None when nothing has been compiled yet.

        A point-read by owner, which is why it is worth having alongside search: it cannot be starved
        by a global top-K the way a vector query measurably can.
        """
        wire = self._request("GET", f"/v1/working-memory/{owner}")
        return (wire or {}).get("text")

    def delta(self, owner: str, since: datetime | str, *, limit: int = 20) -> dict[str, Any]:
        """The resume brief: what changed since ``since``.

        Returns the buckets plus ``taken_at``, which the caller should keep as its next checkpoint.
        Consecutive deltas partition time exactly — the window is half-open — so passing the returned
        instant back means nothing is seen twice and nothing falls between two calls.
        """
        wire = self._request(
            "POST",
            "/v1/delta",
            {"ownerId": owner, "since": _stamp(since), "maxItemsPerSection": limit},
        )
        return {
            "since": _utc(wire["since"]),
            "taken_at": _utc(wire["takenAtUtc"]),
            "new_facts": [self._from_fact(f) for f in wire["newFacts"]],
            "superseded": [
                {"old": self._from_fact(p["old"]), "new": self._from_fact(p["new"])}
                for p in wire["supersededPairs"]
            ],
            "invalidated": [self._from_fact(f) for f in wire["invalidatedFacts"]],
            "truncated_sections": wire["truncatedSections"],
        }
