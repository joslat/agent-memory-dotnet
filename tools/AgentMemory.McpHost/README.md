# agent-memory-mcp

A turnkey MCP server for **AgentMemory for .NET**. It exposes the memory tools over stdio or HTTP,
backed by Neo4j, configured entirely by environment variables.

Before this, the tools shipped with no way to run them without writing a .NET host first — so the only
people who could try them were people who would have built one anyway.

## Install

```bash
dotnet tool install -g AgentMemory.McpHost
```

You need a Neo4j 5.26 instance and one inference provider (Azure OpenAI, Bitdeer, OpenAI, Foundry or any OpenAI-compatible host). If you have neither, the
compose file below starts the database and the server together.

## Run

```bash
export BITDEER_API_KEY=<key>          # chat + embeddings; one variable
export NEO4J_PASSWORD=<password>

agent-memory-mcp                      # stdio, for a desktop MCP client
agent-memory-mcp --transport http     # http://localhost:5233
agent-memory-mcp --read-only          # only the tools that read
agent-memory-mcp --owner alice       # every call runs as one owner
```

Schema migrations run at startup. That matters more than it sounds: **a missing vector index returns
no rows rather than an error**, so a server started without them looks perfectly healthy and answers
nothing. Pass `--no-bootstrap` only when the schema already exists and the database user cannot create
indexes.

## MCP client configuration

```json
{
  "mcpServers": {
    "agent-memory": {
      "command": "agent-memory-mcp",
      "env": {
        "BITDEER_API_KEY": "<key>",
        "NEO4J_PASSWORD": "<password>"
      }
    }
  }
}
```

Logs go to **stderr** on both transports; on stdio, stdout is the JSON-RPC stream and a single stray
line on it corrupts the session.

## Read-only mode

`--read-only` (or `AGENT_MEMORY_MCP_READ_ONLY=true`) removes every tool that writes from the server's
tool list **entirely**, rather than refusing them when called. A tool a client can see is a tool a
model will try, and an error return teaches it nothing about what the server is for.

Nine tools remain: `memory_search`, `memory_get_context`, `memory_get_conversation`,
`memory_list_sessions`, `memory_get_entity`, `memory_get_entity_provenance`, `memory_get_observations`,
`memory_recall_as_of`, `memory_lineage`. With `--enable-graph-query`, `memory_export_graph` and
`memory_find_duplicates` remain as well.

`graph_query` is withheld under `--read-only` even though it is nominally a read. It takes arbitrary
Cypher, and a mode whose entire value is "this cannot change anything" should not rest on a query
parser.

## Graph tools

`graph_query`, `memory_export_graph` and `memory_find_duplicates` read the whole graph, across owners.
They are off by default and **not listed at all** unless `--enable-graph-query` (or
`AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY=true`) is set. Earlier versions listed them always and refused
every call, which taught a model to try a tool that could never work.

## One owner

Without `--owner`, the model chooses the owner: `userId` is an ordinary tool argument, and an omitted
one means shared memory. A model that passes "user" one day and "alice" the next splits your memory in
two, and one that omits it writes into the shared pool.

`--owner alice` (or `AGENT_MEMORY_MCP_OWNER=alice`) pins every call to that owner. `userId` disappears
from the tool schemas, every tool that takes it runs as `alice`, a call that names another owner is
refused, and the list resources (`memory://entities`, `memory://preferences`, `memory://conversations`,
`memory://context/...`) read `alice`'s memory. The graph tools are not listed on a pinned server,
because none of them can be confined to one owner. Tools that address data by a trace or step id, or
by a session id, are not owner-scoped; give each owner's sessions their own ids.

Use it for a personal server. A shared server where each caller is a different person needs an
authenticated host that resolves the caller; the opt-in coding mode does that per project.

## Extraction

`extract_and_persist` and `memory_extract_session` use the store-aware writer (the Recommended
extraction preset) with the provider's chat model. If the provider has no chat model, the two tools are
not listed: earlier versions listed them anyway and ran stub extractors that stored nothing. The startup
line says which: `extraction writer` or `extraction off (reason)`.

The classification lives in `McpToolAccess`, where **writes are the enumerated set** — so a tool added
later and left unclassified is withheld rather than silently exposed, and a guard test fails if any
tool escapes classification altogether.

## Docker

The image targets .NET 10 and builds with the repository SDK pin. Its dedicated `.dockerignore`
includes only public build inputs. For the optional, experimental coding mode, use the separate
[remote coding guide](../../docs/coding-remote.md#container-preparation) and template; that mode
needs pre-provisioned project databases and does not use the Community stack below.

```bash
BITDEER_API_KEY=... AGENT_MEMORY_MCP_OWNER=alice \
  docker compose -f tools/AgentMemory.McpHost/docker-compose.yml up
```

The provider variables you set in your shell pass through; the ones you do not set stay unset in the
container, so auto-detection sees only your provider. Earlier versions required `AZURE_OPENAI_*` here.

Neo4j 5.26 plus the server, from nothing, on `http://localhost:5233`. The compose file waits for the
database to answer before starting the server, because the server bootstraps the schema and exits
non-zero if it cannot — without the healthcheck, a cold volume fails the first run and succeeds the
second, which is the worst possible first impression.

The image defaults to HTTP and binds `0.0.0.0`: inside a container, `localhost` is reachable only from
inside it, which is the most common way a correctly built image looks broken from the host.

**HTTP has no authentication.** Anyone who can reach the port can read and change the memory, so the
compose file publishes every port (the server's and Neo4j's) on `127.0.0.1` only, and the server warns
at startup whenever it listens off localhost. To serve other machines, put it behind an authenticating
proxy.

## Configuration

### Inference provider

The server needs **one** provider, and it needs embeddings — retrieval is its whole job, so a server
that starts without an embedding model does not fail, it answers "nothing found" forever. It fails
closed at startup instead, naming exactly what is missing.

Providers are auto-detected in this order, first complete one wins:

| Provider | Set this |
|---|---|
| Azure OpenAI | `AZURE_OPENAI_ENDPOINT` + `AZURE_OPENAI_API_KEY` + `AZURE_OPENAI_DEPLOYMENT` |
| Bitdeer | `BITDEER_API_KEY` — that alone gets chat *and* embeddings |
| OpenAI | `OPENAI_API_KEY` |
| Foundry | `FOUNDRY_ENDPOINT` + `FOUNDRY_API_KEY` + `FOUNDRY_MODEL` |
| OpenAI-compatible | `OPENAI_COMPATIBLE_ENDPOINT` + `OPENAI_COMPATIBLE_MODEL` (LM Studio, vLLM) |

**Azure is first on purpose.** A server configured before this existed keeps working with no edit.
**Ollama is never auto-detected:** name it with `AI_INFERENCE_PROVIDER=ollama` (+ `OLLAMA_MODEL`), or
move only embeddings to a local bge-m3 with `AI_EMBEDDING_PROVIDER=ollama`.

Name one explicitly with `AI_INFERENCE_PROVIDER=azure|bitdeer|openai|foundry|openai-compatible|ollama`. An
explicit choice that is missing variables **fails** rather than falling through to another host —
falling through would send your API key somewhere you did not pick.

`AI_EMBEDDING_DIMENSIONS` is required when the embedding model's width is not one this package knows.
It is never guessed: the width defines the Neo4j vector index, and a wrong one does not throw — it
builds an index that cannot match its own writes, which surfaces later as recall that finds nothing.

The full contract, including pointing embeddings at a different host from chat, is in
[`docs/configuration/inference-providers.md`](../../docs/configuration/inference-providers.md).

### Everything else

| Variable | Default | |
|---|---|---|
| `NEO4J_PASSWORD` | — | **required** |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | `text-embedding-ada-002` | azure only |
| `NEO4J_URI` | `bolt://localhost:7687` | |
| `NEO4J_USERNAME` | `neo4j` | |
| `NEO4J_DATABASE` | `neo4j` | |
| `AGENT_MEMORY_MCP_TRANSPORT` | `stdio` | `--transport` |
| `AGENT_MEMORY_MCP_URL` | `http://localhost:5233` | `--url` |
| `AGENT_MEMORY_MCP_SERVER_NAME` | `agent-memory` | `--server-name` |
| `AGENT_MEMORY_MCP_READ_ONLY` | unset | `--read-only` |
| `AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY` | unset | `--enable-graph-query` |
| `AGENT_MEMORY_MCP_OWNER` | unset (each call's `userId`) | `--owner` |
| `AGENT_MEMORY_MCP_NO_BOOTSTRAP` | unset | `--no-bootstrap` |
| `AGENT_MEMORY_MCP_LOG_LEVEL` | `information` | `--log-level` |

`NEO4J_PASSWORD` has no default on purpose: a blank password becomes an authentication failure at the
first query, which from an MCP client is indistinguishable from an empty database.

An unrecognised flag is an error rather than being ignored — a typo in `--read-only` would otherwise
start a fully writable server that the operator believes is read-only.
