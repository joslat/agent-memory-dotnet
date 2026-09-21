# Inference providers

AgentMemory's library packages construct **no** model client. Everything flows through
`Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`), so the
library works with any provider you hand it and this page is about the *edges* — the samples, the
`agent-memory-mcp` tool, and the benchmark harness — plus the `AgentMemory.Inference` package they
share, which you can use in your own host.

**One selector, one resolver, one factory, two protocol families.** Azure OpenAI and Foundry speak
the Azure protocol; Bitdeer, OpenAI and any OpenAI-compatible host speak OpenAI's. Everything
downstream sees the `Microsoft.Extensions.AI` interfaces and cannot tell which produced it.

## The short version

| You have | Set |
|---|---|
| A Bitdeer key | `BITDEER_API_KEY` — that alone gets chat **and** embeddings |
| An OpenAI key | `OPENAI_API_KEY` |
| An existing Azure setup | nothing; it already works |
| Ollama / LM Studio / vLLM | `OPENAI_COMPATIBLE_ENDPOINT` + `OPENAI_COMPATIBLE_MODEL` |

## Choosing a provider

`AI_INFERENCE_PROVIDER` ∈ `azure` · `bitdeer` · `openai` · `foundry` · `openai-compatible`.

Leave it unset and providers are auto-detected in a fixed order — **Azure → Bitdeer → OpenAI →
Foundry → OpenAI-compatible** — first one with complete credentials wins.

Four rules govern this, and the second is the one that matters most:

1. **Explicit beats detected.** Name a provider and that is the provider.
2. **An explicit mistake fails closed.** A named provider with missing variables, or an unrecognised
   name, resolves to *nothing*, with the reason. It never falls through to another host. Falling
   through would send your API key somewhere you did not choose, and every call would succeed.
3. **Auto-detect is ordered, and Azure is first on purpose.** A machine that only ever had
   `AZURE_OPENAI_*` behaves exactly as it did before this layer existed. A test pins that.
4. **A half-configured provider is a typo, not an unchosen one.** The error names the missing
   variables *of that provider*, not a generic "nothing configured".

## Chat

| Provider | Required | Optional (default) |
|---|---|---|
| `azure` | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT` | `AZURE_OPENAI_DEPLOYMENT_2`, `_3` |
| `bitdeer` | `BITDEER_API_KEY` | `BITDEER_ENDPOINT` (`https://api-inference.bitdeer.ai/v1`), `BITDEER_MODEL` (`zai-org/GLM-5.3-Flash`), `_2`, `_3` |
| `openai` | `OPENAI_API_KEY` | `OPENAI_BASE_URL` (`https://api.openai.com/v1`), `OPENAI_MODEL` (`gpt-4o-mini`), `_2`, `_3` |
| `foundry` | `FOUNDRY_ENDPOINT`, `FOUNDRY_API_KEY`, `FOUNDRY_MODEL` | `FOUNDRY_MODEL_2`, `_3` |
| `openai-compatible` | `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL` | `OPENAI_COMPATIBLE_API_KEY` (`no-key-needed` for a keyless local server), `_2`, `_3` |

For Azure, the "model" is a **deployment name**. Everywhere else it is a model id.

> **`_2` and `_3` are carried for contract parity and nothing in AgentMemory reads them yet.** They
> exist so one operator configures this repository and AgentEval identically — a value that works
> there must not fail closed here. But setting `BITDEER_MODEL_2` changes nothing about an
> AgentMemory run today: the benchmark arms select by ingestion and retrieval tokens, not by a
> second chat model. Stated rather than implied, because a setting that looks live and is not is
> worse than one that is plainly documented as reserved.

## Embeddings, and why the dimension is not optional

AgentMemory's vector search is core, so every provider needs an embedding model — and the
**dimension is store-defining**. It becomes `Neo4jOptions.EmbeddingDimensions`, which builds the
Neo4j vector index.

> **A wrong dimension does not throw.** It builds an index that cannot match the vectors written
> into it. Nothing fails at startup; recall simply returns nothing, and it surfaces weeks later as
> "the memory doesn't work". That is why the dimension is **never guessed**.

| Provider | Variable | Default |
|---|---|---|
| `azure` | `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | `text-embedding-ada-002` |
| `bitdeer` | `BITDEER_EMBEDDING_MODEL` | `BAAI/bge-m3` |
| `openai` | `OPENAI_EMBEDDING_MODEL` | `text-embedding-3-small` |
| `foundry` | `FOUNDRY_EMBEDDING_MODEL` | *(required)* |
| `openai-compatible` | `OPENAI_COMPATIBLE_EMBEDDING_MODEL` | *(required)* |

Widths this package knows: `text-embedding-ada-002` 1536 · `text-embedding-3-small` 1536 ·
`text-embedding-3-large` 3072 · `BAAI/bge-m3` **1024** · `nomic-embed-text` 768 ·
`Qwen/Qwen3-Embedding-0.6B` 1024 · `Qwen/Qwen3-Embedding-4B` 2560 · `Qwen/Qwen3-Embedding-8B` 4096.

Anything else: set **`AI_EMBEDDING_DIMENSIONS`**. It also *overrides* the table, for a deployment
configured at a non-default width.

> **Changing embedding model means a new store.** 1536-wide vectors and 1024-wide vectors cannot
> share an index. Moving from Azure's ada-002 to Bitdeer's bge-m3 is a rebuild, not a config change.

### Embeddings somewhere else entirely

For "chat on Bitdeer, embeddings on a local Ollama", set all four together:

```bash
AI_EMBEDDING_PROVIDER=openai-compatible
AI_EMBEDDING_ENDPOINT=http://localhost:11434/v1
AI_EMBEDDING_API_KEY=no-key-needed
AI_EMBEDDING_MODEL=nomic-embed-text
```

A **partial** block fails the embedding half by name rather than quietly falling back to the chat
host — you were moving embeddings deliberately, and a silent fallback would build the store on the
model you were moving away from.

## Roles

| Variable | Meaning | Default |
|---|---|---|
| `AGENTMEMORY_EXTRACTION_MODEL` | the extraction model | the primary model |
| `AI_JUDGE_PROVIDER` / `_ENDPOINT` / `_API_KEY` / `_MODEL` | the judge, independent of the subject; all four together | the primary model |
| `AZURE_OPENAI_EXTRACTION_DEPLOYMENT` | honoured as an alias **when the provider is `azure`** | — |
| `AZURE_OPENAI_JUDGE_ENDPOINT` / `_API_KEY` / `_DEPLOYMENT` | AgentEval's Azure-shaped judge override, kept for parity | — |

> Without a judge override the judge runs **on the model under test**. That is a fine default and a
> thing worth knowing when reading a score it produced.

## Endpoints and secrets

- **https anywhere; http only to loopback.** An http endpoint to a remote host would put your API
  key on the wire in cleartext, so it is refused rather than warned about.
- **Anything printed is reduced to scheme, host and port.** User-info, path, query and fragment are
  all places a token turns up — `https://gw.example/v1/sk-live-…` reads as an ordinary endpoint and
  is a secret. Banners, diagnostics and logs never show them.
- **Diagnostics name variables, never values.**

## Diagnostics

| Variable | Effect |
|---|---|
| `AGENTMEMORY_INFERENCE_NETWORK_TIMEOUT_S` | per-attempt timeout for the subject under test (default `180`) |
| `AGENTMEMORY_INFERENCE_SHOW_RAW` | `1` prints every request and reply to **stderr**, key and URI scrubbed, one atomic write per call |

A judge always gets a generous timeout regardless: timing one out does not protect a measurement, it
discards one already paid for.

## Run identity

Every measured number is stamped **`model@provider`**, and embeddings **`model@provider/dims`**.

The same model id served by two hosts is not the same measurement — different quantisation, serving
stack and sampling defaults. Without the host in the identity, two such runs are indistinguishable
in the artifact, which is the kind of thing that invalidates a comparison long after it is published.

## Using it in your own host

```csharp
services.AddAgentMemoryInferenceFromEnvironment();
```

Registers `InferenceProviderSettings`, `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>`,
and **fails at registration** rather than at first use — a misconfigured host that starts cleanly and
throws on the first user turn has moved the error from the operator who can fix it to the user who
cannot.

Apply the width to your store yourself; the package holds no reference to AgentMemory:

```csharp
var settings = provider.GetRequiredService<InferenceProviderSettings>();
services.AddNeo4jAgentMemory(o => o.EmbeddingDimensions = settings.EmbeddingDimensions!.Value);
```

Pass `configure => configure.RequireEmbeddings = false` for a chat-only host.
