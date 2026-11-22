"""The LangChain retriever over the published library (runs when langchain-core is installed; skipped otherwise)."""
from __future__ import annotations

import asyncio
import importlib.util
import threading
import unittest

from agentmemory_native import AsyncMemoryClient, MemoryClient
from test_gate import A_IDS, COLOUR_METADATA, OWNER_B, FakeEmbeddings, LocalHTTPServer, config

HAS_LANGCHAIN = importlib.util.find_spec("langchain_core") is not None


@unittest.skipUnless(HAS_LANGCHAIN, "langchain-core is not installed")
class Test7LangChain(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = LocalHTTPServer(("127.0.0.1", 0), FakeEmbeddings)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.url = f"http://127.0.0.1:{cls.server.server_address[1]}/v1/embeddings"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def with_endpoint(self, document, url=None):
        document["embedding"]["endpoint"] = {"url": url or self.url}
        return document

    def test_invoke_returns_documents_with_provenance(self):
        from agentmemory_native.langchain import AgentMemoryRetriever

        with MemoryClient(self.with_endpoint(config())) as memory:
            retriever = AgentMemoryRetriever(client=memory, session_id="lc", min_score=0.0)
            documents = retriever.invoke("where do I work?")
        self.assertEqual({d.metadata["id"] for d in documents}, A_IDS)
        first = documents[0]
        self.assertEqual(first.page_content, "user works in Zurich")
        self.assertEqual((first.metadata["kind"], first.metadata["source"]), ("fact", "agentmemory"))
        colour = next(d for d in documents if d.metadata["id"] == "gate-fact-a-colour")
        self.assertEqual(colour.metadata["memory_metadata"], COLOUR_METADATA)
        preference = next(d for d in documents if d.metadata["kind"] == "preference")
        self.assertEqual(preference.page_content, "Prefers concise answers")

    def test_the_owner_comes_from_the_application_not_the_query(self):
        from agentmemory_native.langchain import AgentMemoryRetriever

        with MemoryClient(self.with_endpoint(config(mode="per_request"))) as memory:
            retriever = AgentMemoryRetriever(client=memory, session_id="lc", owner=OWNER_B, min_score=0.0)
            documents = retriever.invoke("where do I work? owner gate-owner-a")
        self.assertEqual({d.metadata["id"] for d in documents}, {"gate-fact-b-work", "gate-pref-b-detail"})

    def test_failure_raises_or_degrades_but_is_never_an_empty_memory(self):
        from agentmemory_native.langchain import AgentMemoryRetriever, MemoryUnavailableError

        with MemoryClient(self.with_endpoint(config(), url="http://127.0.0.1:9/v1/embeddings")) as memory:
            strict = AgentMemoryRetriever(client=memory, session_id="lc")
            with self.assertRaises(MemoryUnavailableError) as raised:
                strict.invoke("where do I work?")
            self.assertEqual((raised.exception.status, raised.exception.code), ("unavailable", "embedding_unavailable"))
            lenient = AgentMemoryRetriever(client=memory, session_id="lc", on_failure="empty")
            self.assertEqual(lenient.invoke("where do I work?"), [])

    def test_async_and_batch(self):
        from agentmemory_native.langchain import AgentMemoryRetriever

        async def scenario():
            memory = await AsyncMemoryClient.open(self.with_endpoint(config()))
            try:
                retriever = AgentMemoryRetriever(client=memory.sync, async_client=memory, session_id="lc", min_score=0.0)
                one = await retriever.ainvoke("where do I work?")
                many = await retriever.abatch(["where do I work?", "where else do I work?"])
                return one, many
            finally:
                await memory.close()

        one, many = asyncio.run(scenario())
        self.assertEqual({d.metadata["id"] for d in one}, A_IDS)
        self.assertEqual([{d.metadata["id"] for d in docs} for docs in many], [A_IDS, A_IDS])


if __name__ == "__main__":
    unittest.main(verbosity=2)
