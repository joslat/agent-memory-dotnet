# agent-memory-mcp

A turnkey MCP server for **AgentMemory for .NET**. It exposes the 25 memory tools over stdio or HTTP,
backed by Neo4j, configured entirely by environment variables.

Before this, the tools shipped with no way to run them without writing a .NET host first — so the only
people who could try them were people who would have built one anyway.

## Install

```bash
dotnet tool install -g AgentMemory.McpHost
```

You need a Neo4j 5.26 instance and an Azure OpenAI embedding deployment. If you have neither, the
compose file below starts the database and the server together.

## Run

```bash
export AZURE_OPENAI_ENDPOINT=https://<resource>.openai.azure.com/
export AZURE_OPENAI_API_KEY=<key>
export NEO4J_PASSWORD=<password>

agent-memory-mcp                      # stdio, for a desktop MCP client
agent-memory-mcp --transport http     # http://localhost:5233
agent-memory-mcp --read-only          # only the 9 tools that read
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
        "AZURE_OPENAI_ENDPOINT": "https://<resource>.openai.azure.com/",
        "AZURE_OPENAI_API_KEY": "<key>",
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
`memory_export_graph`, `memory_find_duplicates`.

`graph_query` is withheld under `--read-only` even though it is nominally a read. It takes arbitrary
Cypher, and a mode whose entire value is "this cannot change anything" should not rest on a query
parser. It is off by default in any case; `--enable-graph-query` turns it on for a read-write server.

The classification lives in `McpToolAccess`, where **writes are the enumerated set** — so a tool added
later and left unclassified is withheld rather than silently exposed, and a guard test fails if any
tool escapes classification altogether.

## Docker

```bash
AZURE_OPENAI_ENDPOINT=... AZURE_OPENAI_API_KEY=... \
  docker compose -f tools/AgentMemory.McpHost/docker-compose.yml up
```

Neo4j 5.26 plus the server, from nothing, on `http://localhost:5233`. The compose file waits for the
database to answer before starting the server, because the server bootstraps the schema and exits
non-zero if it cannot — without the healthcheck, a cold volume fails the first run and succeeds the
second, which is the worst possible first impression.

The image defaults to HTTP and binds `0.0.0.0`: inside a container, `localhost` is reachable only from
inside it, which is the most common way a correctly built image looks broken from the host.

## Configuration

| Variable | Default | |
|---|---|---|
| `AZURE_OPENAI_ENDPOINT` | — | **required** |
| `AZURE_OPENAI_API_KEY` | — | **required** |
| `NEO4J_PASSWORD` | — | **required** |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | `text-embedding-3-small` | |
| `NEO4J_URI` | `bolt://localhost:7687` | |
| `NEO4J_USERNAME` | `neo4j` | |
| `NEO4J_DATABASE` | `neo4j` | |
| `AGENT_MEMORY_MCP_TRANSPORT` | `stdio` | `--transport` |
| `AGENT_MEMORY_MCP_URL` | `http://localhost:5233` | `--url` |
| `AGENT_MEMORY_MCP_SERVER_NAME` | `agent-memory` | `--server-name` |
| `AGENT_MEMORY_MCP_READ_ONLY` | unset | `--read-only` |
| `AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY` | unset | `--enable-graph-query` |
| `AGENT_MEMORY_MCP_NO_BOOTSTRAP` | unset | `--no-bootstrap` |
| `AGENT_MEMORY_MCP_LOG_LEVEL` | `information` | `--log-level` |

`NEO4J_PASSWORD` has no default on purpose: a blank password becomes an authentication failure at the
first query, which from an MCP client is indistinguishable from an empty database.

An unrecognised flag is an error rather than being ignored — a typo in `--read-only` would otherwise
start a fully writable server that the operator believes is read-only.
