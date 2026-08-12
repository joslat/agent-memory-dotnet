# Copilot Studio custom connector (D6)

A custom-connector definition pointing Microsoft Copilot Studio at the AgentMemory MCP server, so a
Copilot Studio agent gets persistent graph-backed memory across sessions.

**This lane is open.** No memory provider in the competitive survey ships a Copilot Studio
integration, and the only gate this task ever had — "there is no installable server with an HTTP
transport to point at" — closed when the `dotnet tool` MCP host and its container image landed.

## What is here, and what is not

| | Status |
|---|---|
| Connector definition (`apiDefinition.swagger.json`) | ✅ In this repo |
| Import + configuration steps | ✅ Below |
| **Validation in a real Copilot Studio tenant** | ❌ **Not done — needs a tenant** |

The definition is written against the documented `mcp-streamable-1.0` agentic-protocol contract and
the route this server actually serves, but **it has not been imported into a live tenant**. Treat the
steps below as untested-in-anger until someone runs them. Saying so is the point: a connector that
looks right and has never been imported is exactly the kind of thing that gets announced and then
fails on first contact.

## 1. Run the MCP host with the HTTP transport

```bash
dotnet tool install --global AgentMemory.McpHost
agentmemory-mcp --transport http --http-url http://0.0.0.0:8080 \
  --neo4j-uri bolt://your-neo4j:7687
```

Or the container image, whose `docker-compose.yml` sits beside the host project.

`MapMcp()` serves the MCP endpoint at the **root path**, which is why the connector's single
operation is `POST /`.

## 2. Expose it over HTTPS

Copilot Studio will not call a plaintext endpoint. Put the host behind a reverse proxy or an Azure
Container App with a TLS ingress, and set `host` in `apiDefinition.swagger.json` to that hostname.

> **The host has no authentication of its own.** The `api_key` security definition forwards an
> `Authorization` header, but nothing in the MCP host validates it — enforcement has to live in the
> proxy in front. Exposing the host directly to the internet publishes read *and write* access to
> every memory it holds. This is the one step that is genuinely dangerous to skim.

## 3. Import the connector

Power Apps / Power Automate → **Custom connectors** → **New custom connector** → *Import an OpenAPI
file* → select `apiDefinition.swagger.json`.

## 4. Add it to an agent

Copilot Studio → your agent → **Tools** → **Add a tool** → **Model Context Protocol** → pick the
connector. The tool list is discovered from the server at runtime rather than declared in the
definition, so `memory_search`, `memory_add_entity` and the rest appear without the swagger listing
them one by one — and stay correct when the server's tool surface changes.

## Which tools an agent gets

Governed by the host's own access flags, not by this connector:

- `--read-only` restricts the surface to recall and inspection.
- `--enable-graph-query` opts into raw Cypher (`nams_graph_query` remains deferred).

Configure that at the host. A connector cannot narrow what the server offers, so an agent that must
not write needs a host started `--read-only` rather than a connector that merely omits the tools.

## Related

- [MCP server tools](../../docs/memory-map.md)
- [Security notes](../../docs/security)
