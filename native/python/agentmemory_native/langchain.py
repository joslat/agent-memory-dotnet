"""A LangChain retriever over the AgentMemory native library (``pip install agentmemory-native[langchain]``).

    from agentmemory_native import MemoryClient
    from agentmemory_native.langchain import AgentMemoryRetriever

    memory = MemoryClient(config)                       # config with an embedding endpoint, so text queries work
    retriever = AgentMemoryRetriever(client=memory, session_id="session-1")
    documents = retriever.invoke("where do I work?")

Each fact or preference becomes one Document whose metadata says where it came from (id, kind, confidence, the stored
metadata). The owner is never taken from the query: it is the context's configured owner, or, for a context opened with
``per_request`` owners, the ``owner`` the application sets on the retriever.
"""
from __future__ import annotations

from typing import Any, Mapping

from langchain_core.callbacks import AsyncCallbackManagerForRetrieverRun, CallbackManagerForRetrieverRun
from langchain_core.documents import Document
from langchain_core.retrievers import BaseRetriever
from pydantic import ConfigDict

from .client import AsyncMemoryClient, MemoryClient
from .results import ContextResult


class MemoryUnavailableError(RuntimeError):
    """The recall did not run to completion; ``status`` and ``code`` say why (timeout, unavailable, invalid, error)."""

    def __init__(self, result: ContextResult) -> None:
        self.status = result.status
        self.code = result.error.code if result.error else ""
        super().__init__(f"AgentMemory recall {result.status}: {self.code} {result.error.message if result.error else ''}".strip())


def _plain(value: Any) -> Any:
    """Read-only result metadata as plain JSON values, which LangChain documents and tracers expect."""
    if isinstance(value, Mapping):
        return {key: _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list)):
        return [_plain(item) for item in value]
    return value


def documents_from(result: ContextResult) -> list[Document]:
    """Facts first, then preferences, each in the order recall ranked them."""
    documents = [
        Document(
            page_content=f"{fact.subject} {fact.predicate.replace('_', ' ')} {fact.object}",
            metadata={
                "id": fact.id, "kind": "fact", "subject": fact.subject, "predicate": fact.predicate,
                "object": fact.object, "confidence": fact.confidence, "category": fact.category,
                "valid_from": fact.valid_from, "valid_until": fact.valid_until, "created_at": fact.created_at,
                "memory_metadata": _plain(fact.metadata), "source": "agentmemory",
            },
        )
        for fact in result.facts
    ]
    documents.extend(
        Document(
            page_content=preference.text,
            metadata={
                "id": preference.id, "kind": "preference", "category": preference.category,
                "context": preference.context, "confidence": preference.confidence,
                "created_at": preference.created_at, "memory_metadata": _plain(preference.metadata),
                "source": "agentmemory",
            },
        )
        for preference in result.preferences
    )
    return documents


class AgentMemoryRetriever(BaseRetriever):
    """Owner-scoped AgentMemory recall as a LangChain retriever.

    ``on_failure="raise"`` (default) raises MemoryUnavailableError when a recall times out, cannot reach the store or
    the embedding endpoint, or is refused; ``"empty"`` returns no documents instead, for chains that must keep going
    without memory. Either way a failure is never mistaken for "nothing stored".
    """

    client: MemoryClient
    session_id: str
    owner: str | None = None
    async_client: AsyncMemoryClient | None = None
    max_facts: int = 5
    max_preferences: int = 5
    min_score: float | None = None
    timeout_ms: int = 0
    on_failure: str = "raise"

    model_config = ConfigDict(arbitrary_types_allowed=True)

    def _arguments(self, query: str) -> dict[str, Any]:
        return {"session_id": self.session_id, "query": query, "owner": self.owner, "max_facts": self.max_facts,
                "max_preferences": self.max_preferences, "min_score": self.min_score}

    def _finish(self, result: ContextResult) -> list[Document]:
        if result.ok:
            return documents_from(result)
        if self.on_failure == "empty":
            return []
        raise MemoryUnavailableError(result)

    def _get_relevant_documents(self, query: str, *, run_manager: CallbackManagerForRetrieverRun) -> list[Document]:
        return self._finish(self.client.get_context(timeout_ms=self.timeout_ms, **self._arguments(query)))

    async def _aget_relevant_documents(
        self, query: str, *, run_manager: AsyncCallbackManagerForRetrieverRun
    ) -> list[Document]:
        if self.async_client is None:
            return await super()._aget_relevant_documents(query, run_manager=run_manager)
        return self._finish(await self.async_client.get_context(timeout_ms=self.timeout_ms, **self._arguments(query)))
