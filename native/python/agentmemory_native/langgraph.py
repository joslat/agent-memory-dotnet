"""A LangGraph store over the AgentMemory native library (``pip install langgraph`` beside ``agentmemory-native``).

    from agentmemory_native import MemoryClient
    from agentmemory_native.langgraph import AgentMemoryStore

    memory = MemoryClient(config)          # "explicit_write" or "learning" for puts and deletes; "read_only" for search
    store = AgentMemoryStore(memory)
    store.put(("user", "prefs"), "drink", {"text": "I prefer tea to coffee."})
    hits = store.search(("user",), query="what do I like to drink?")

What it is, plainly: AgentMemory is not a document store. A ``put`` remembers the words in ``value["text"]`` (or
``value["content"]``): the library's writer turns them into facts and preferences of the context's one owner, through
its durable journal. A ``get`` returns that remember's receipt (its state and the memories it produced), not the
words. A ``search`` returns the facts and preferences recall finds for the query (one item each, keyed by memory id),
from every key, because memories are the owner's, not the key's. A delete (``put`` with ``None``) forgets: the memories
that key produced are closed, not deleted. A ``put`` with other words under the same key forgets the earlier ones and
remembers the new words. The namespace and key only name the source; they never choose the owner.
"""
from __future__ import annotations

import asyncio
import datetime as dt
from collections.abc import Iterable
from typing import Any

from langgraph.store.base import BaseStore, GetOp, Item, ListNamespacesOp, Op, PutOp, Result, SearchItem, SearchOp

from .client import MemoryClient
from .results import Receipt

__all__ = ["AgentMemoryStore", "StoreWriteError"]

#: Receipt states whose memories are no longer used: a key in one of them reads as absent.
GONE = frozenset({"rejected", "cancelled", "forgotten", "erased"})


class StoreWriteError(RuntimeError):
    """A put or delete was not accepted, or ended without its memories in use; ``code`` says why."""

    def __init__(self, code: str, message: str = "") -> None:
        self.code = code
        super().__init__(f"AgentMemory {code}" + (f": {message}" if message else ""))


def _time(value: str | None) -> dt.datetime:
    if not value:
        return dt.datetime.now(dt.timezone.utc)
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00"))


class AgentMemoryStore(BaseStore):
    """LangGraph ``BaseStore`` operations on one AgentMemory context (its configured owner).

    ``wait_ms`` bounds how long a put waits for its write to end; past it the put returns and the write goes on (read
    it with ``get``). A put whose write ends rejected raises StoreWriteError, unless ``raise_on_rejected`` is False.
    """

    def __init__(self, client: MemoryClient, *, session_id: str = "langgraph", wait_ms: int = 60_000,
                 min_score: float | None = None, raise_on_rejected: bool = True) -> None:
        self.client = client
        self.session_id = session_id
        self.wait_ms = wait_ms
        self.min_score = min_score
        self.raise_on_rejected = raise_on_rejected

    # Sources: one per key and revision, so other words under the same key are a new source, never a conflict.
    @staticmethod
    def _source(namespace: tuple[str, ...], key: str, revision: int) -> str:
        return "langgraph/" + "/".join(namespace) + f"/{key}/rev-{revision}"

    def _latest(self, namespace: tuple[str, ...], key: str) -> tuple[int, Receipt] | None:
        found = None
        revision = 1
        while True:
            result = self.client.operation(source_id=self._source(namespace, key, revision))
            if not result.ok or result.receipt is None:
                return found
            found = (revision, result.receipt)
            revision += 1

    def _item(self, namespace: tuple[str, ...], key: str, receipt: Receipt) -> Item:
        value = {"state": receipt.state, "remembered": receipt.remembered, "facts": list(receipt.facts),
                 "preferences": list(receipt.preferences), "operation_id": receipt.operation_id}
        return Item(value=value, key=key, namespace=namespace, created_at=_time(receipt.accepted_at),
                    updated_at=_time(receipt.finished_at or receipt.accepted_at))

    def _get(self, op: GetOp) -> Item | None:
        latest = self._latest(op.namespace, op.key)
        if latest is None or latest[1].state in GONE:
            return None
        return self._item(op.namespace, op.key, latest[1])

    def _forget(self, receipt: Receipt) -> None:
        if receipt.state in GONE:
            return
        result = self.client.forget(operation_id=receipt.operation_id, timeout_ms=self.wait_ms)
        if not result.ok:
            raise StoreWriteError(result.error.code if result.error else result.status,
                                  result.error.message if result.error else "")

    def _put(self, op: PutOp) -> None:
        latest = self._latest(op.namespace, op.key)
        if op.value is None:
            if latest is not None:
                self._forget(latest[1])
            return
        text = op.value.get("text", op.value.get("content"))
        if not isinstance(text, str) or not text.strip():
            raise ValueError('AgentMemoryStore remembers words: put a value with a non-empty "text" (or "content").')
        now = dt.datetime.now(dt.timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        if latest is None:
            revision, said = 1, now
        elif latest[1].state in GONE:
            # Forgotten or erased: putting it again is a new remember, never the old receipt.
            revision, said = latest[0] + 1, now
        else:
            # The key's current words keep their original time, so the same words again are the same source (the
            # library returns its receipt and runs nothing); other words are refused as a conflict, handled below.
            revision, said = latest[0], latest[1].occurred_at
        result = self.client.remember(source_id=self._source(op.namespace, op.key, revision), session_id=self.session_id,
                                      text=text, occurred_at=said, timeout_ms=self.wait_ms)
        if not result.ok and result.error is not None and result.error.code == "source_conflict" and latest is not None:
            # Other words under the same key: stop using the earlier ones, then remember these as the next revision.
            self._forget(latest[1])
            result = self.client.remember(source_id=self._source(op.namespace, op.key, revision + 1),
                                          session_id=self.session_id, text=text, occurred_at=now,
                                          timeout_ms=self.wait_ms)
        if not result.ok or result.receipt is None:
            raise StoreWriteError(result.error.code if result.error else result.status,
                                  result.error.message if result.error else "")
        if result.receipt.state == "rejected" and self.raise_on_rejected:
            raise StoreWriteError("rejected", result.receipt.reason or "")

    def _search(self, op: SearchOp) -> list[SearchItem]:
        if not op.query:
            # AgentMemory recalls by meaning; it has no listing of every memory.
            return []
        count = max(1, op.limit + op.offset)
        result = self.client.get_context(session_id=self.session_id, query=op.query, max_facts=min(count, 50),
                                         max_preferences=min(count, 50), min_score=self.min_score)
        if not result.ok:
            raise StoreWriteError(result.error.code if result.error else result.status,
                                  result.error.message if result.error else "")
        items: list[SearchItem] = []
        for fact in result.facts:
            items.append(SearchItem(
                namespace=("agentmemory", "facts"), key=fact.id,
                value={"kind": "fact", "text": f"{fact.subject} {fact.predicate.replace('_', ' ')} {fact.object}",
                       "subject": fact.subject, "predicate": fact.predicate, "object": fact.object,
                       "confidence": fact.confidence, "valid_from": fact.valid_from, "valid_until": fact.valid_until},
                created_at=_time(fact.created_at), updated_at=_time(fact.created_at)))
        for preference in result.preferences:
            items.append(SearchItem(
                namespace=("agentmemory", "preferences"), key=preference.id,
                value={"kind": "preference", "text": preference.text, "category": preference.category,
                       "confidence": preference.confidence},
                created_at=_time(preference.created_at), updated_at=_time(preference.created_at)))
        return items[op.offset:op.offset + op.limit]

    def batch(self, ops: Iterable[Op]) -> list[Result]:
        results: list[Result] = []
        for op in ops:
            if isinstance(op, GetOp):
                results.append(self._get(op))
            elif isinstance(op, PutOp):
                self._put(op)
                results.append(None)
            elif isinstance(op, SearchOp):
                results.append(self._search(op))
            elif isinstance(op, ListNamespacesOp):
                # Search results live in these two namespaces; puts may use any namespace as a source label.
                results.append([("agentmemory", "facts"), ("agentmemory", "preferences")][op.offset:op.offset + op.limit])
            else:
                raise NotImplementedError(f"AgentMemoryStore does not support {type(op).__name__}")
        return results

    async def abatch(self, ops: Iterable[Op]) -> list[Result]:
        # Native calls never run on the event loop.
        return await asyncio.to_thread(self.batch, list(ops))
