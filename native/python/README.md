# agentmemory-native

AgentMemory for Python, without installing .NET: the AgentMemory library compiled with Native AOT into one shared
library, with a small Python binding (ctypes, standard library only). It recalls a person's memories from Neo4j and,
in the writing profiles, turns what they say into facts and preferences.

It is the memory behind a voice assistant: a speech server recalls before each reply and saves after the person's
turn, within a time limit it sets (about 100 ms a recall with a local embedding model).

## Requirements

- Linux x86-64 with glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+), CPython 3.10 or newer.
- Neo4j 5.26 with the AgentMemory schema.
- An OpenAI-compatible embeddings endpoint (for example Ollama with `bge-m3`), and for writing, an
  OpenAI-compatible chat model.

## Install

```bash
pip install agentmemory-native
```

## Recall

```python
import agentmemory_native as am

config = {
    "schemaVersion": 1,
    "profile": "read_only",
    "owner": {"mode": "fixed", "id": "user-42"},
    "store": {"uri": "bolt://localhost:7687", "username": "neo4j", "password": "your-password"},
    "embedding": {"dimensions": 1024, "model": "bge-m3",
                  "endpoint": {"url": "http://localhost:11434/v1/embeddings"}},
}

with am.MemoryClient(config) as memory:
    result = memory.get_context(session_id="conversation-1", query="Where do I work?",
                                max_facts=10, max_preferences=5, min_score=0.7,
                                max_entities=10, max_relationships=10, time_windows=True)
    for fact in result.facts:
        print(fact.subject, fact.predicate, fact.object)
```

Recall reads one owner's memory only. `AsyncMemoryClient` offers the same calls for asyncio.

## Remember

With the `explicit_write` or `learning` profile, a `writer` (any OpenAI-compatible chat model) and a `state`
directory for the write journal, `memory.remember(source_id=..., session_id=..., text=..., occurred_at=...)` saves
what the person said and returns a receipt: what was kept, and whether recall can find it yet. Forgetting and erasing
an owner are calls of their own.

## The contract

Requests and results are JSON documents, versioned (ABI 1, schema version 1). The full contract, the error codes and
the configuration are in
[native/CONTRACT.md](https://github.com/joslat/agent-memory-dotnet/blob/main/native/CONTRACT.md).

## Licence

MIT. The wheel carries the licences of the packages compiled into the library (`licenses/THIRD-PARTY-NOTICES.txt`)
and a manifest of the library's build (`agentmemory_native/_native/MANIFEST.json`); the binding refuses a library whose
bytes differ from it.
