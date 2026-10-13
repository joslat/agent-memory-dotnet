# Remote coding memory over HTTPS/OAuth

The coding host can act as a personal MCP resource server. It uses the same scoped memory operations,
writer and pre-provisioned project databases as [local coding mode](coding-service.md). The remote
transport has been validated in an isolated local Docker deployment against a real Keycloak 26.8.0
issuer, Chrome authorization-code/PKCE login and consent, and real Neo4j storage. A dedicated test
client exercised HTTPS MCP discovery, read/write scopes, wrong-owner/project rejection, token expiry
and refresh, and a synthetic Bitdeer-backed write followed by new-session recall. Stop/start persistence,
non-destructive forgetting and server-side write opt-out were also exercised. This is local issuer/host
interoperability evidence; the actual Claude cloud connector remains unvalidated.

Separate synthetic HTTPS issuer tests exercise cached-key use during an outage, automatic refresh for
a new signing key, and rejection of insecure key URLs/untrusted certificates. Those rotation/outage
cases have not been repeated against the real issuer. The local deployment uses pre-registered clients,
a lab CA trusted only by the test processes, and browser exceptions pinned to the two lab certificate
keys. It does not establish public certificate trust, dynamic registration or internet deployment readiness.

This is one service per owner. An external authorization server handles sign-in, consent, OAuth
clients and refresh tokens. The memory host validates access tokens; it does not issue them or forward
them to inference providers. The MCP resource-server/discovery roles follow the
[MCP authorization specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization).

## Configuration

Start with [remote-service.example.json](../samples/CodingMemory/remote-service.example.json), stored
privately, and use the existing `agent-memory-mcp --coding-config <private.json>` command. The example
does not start anything, and its listener is loopback until deliberately changed. Set:

- `ownerId`: the memory owner's fixed identifier. OAuth subjects never become caller-supplied owners.
- `url`: the public HTTPS origin, including the port if needed. No path, credentials or query string.
- `projects`: canonical project IDs with empty values. Remote mode accepts OAuth only, not local tokens.
- `remote.issuer`: the exact HTTPS issuer with OIDC discovery and signing keys.
- `remote.subject`: the exact `sub` for the owner at that issuer. Other accounts cannot use this service.
- `remote.listenAddress`: an explicit bind IP. Choose the intended interface when deploying.
- `remote.certificatePath`: an absolute path to an encrypted PFX certificate with private key. Change
  the sample's Linux path on Windows, for example `C:/private/memory.pfx`.
- `remote.certificatePasswordVariable`: the environment variable containing the PFX password.
- `remote.allowWrites`: false by default. True permits mutations only when the token also grants write scope.

The public hostname must have a client-trusted TLS certificate and route to that listener's port.
Kestrel terminates TLS directly. TLS passthrough can preserve this arrangement; HTTP termination with
`X-Forwarded-*` headers is unsupported. Ambient ASP.NET listener configuration is cleared. There is
one listener, a maximum of 64 simultaneous connections, and the existing 64 KiB request-body bound.
Certificate changes require a restart. Browser UI is rejected in remote configuration; local hooks
continue to target the separate local HTTP mode.

The local guide's explicit inference provider, writer model, credentials, project database setup and
input limits still apply. The service bootstraps existing project databases before listening. It does
not create a public hostname, install certificates, register an OAuth client or provision a deployment.

## Container preparation

Build from the repository root:

```sh
docker build -f tools/AgentMemory.McpHost/Dockerfile -t agent-memory-coding:local .
```

The image uses the .NET 10 SDK pinned by `global.json` and the .NET 10 ASP.NET runtime. Its Dockerfile
has a dedicated context allowlist: public build inputs only; strategy, Git state, datasets, generated
output and certificate/key files are excluded. Runtime configuration and credentials are mounted later.

[docker-compose.remote.yml](../samples/CodingMemory/docker-compose.remote.yml) is a preparation
template for this image with an existing Neo4j deployment that supports multiple databases. Provision
`coding-<project>` databases separately. The generic MCP compose stack's Community database is not
suitable for this isolation mode.

Prepare three private files and set their absolute paths in `CODING_CONFIG_FILE`, `CODING_ENV_FILE`
and `CODING_CERTIFICATE_FILE`. The environment file contains only the intended provider, model,
embedding dimensions, Neo4j credentials and `MEMORY_TLS_PASSWORD` variables from the local guide.
For this template, set the JSON `url` to `https://<hostname>:8443`, `remote.listenAddress` to `0.0.0.0`,
and the certificate path to `/run/secrets/memory.pfx`. The container listens on 8443, and the template
publishes that port only on host loopback. This does not make it reachable from Claude's cloud.

Validate without starting the stack:

```sh
docker compose -f samples/CodingMemory/docker-compose.remote.yml config --quiet
```

When the deployment and provider data permissions are ready, start that same compose file with
`up -d --build`. Set the deployment's public routing and certificate hostname consistently with the
configured resource URL; changing the origin also changes token audiences. Explicitly configure the
host binding if direct remote access is intended. Keep TLS to the container; forwarded HTTP is rejected.

The process runs as UID 1654, with a read-only root filesystem, all Linux capabilities dropped,
`no-new-privileges`, a bounded temporary filesystem and read-only config/certificate mounts. Make
the mounted files readable by that UID without exposing them to unrelated users. No Neo4j or provider
credentials are embedded in the image. Loopback inference endpoints refer to the container itself;
they do not reach an Ollama server running on the Docker host.

A local smoke test boots this image with these restrictions, an isolated test database and synthetic
credentials, then checks TLS resource discovery and unauthenticated MCP rejection. The test network
has no internet egress and makes no inference calls. This validates container startup, not a production
deployment or a complete OAuth browser flow. The template supplies no restart policy: restarting loses
in-process jobs, and a deployment must account for that explicitly.

## Authorization-provider contract

Configure and verify the external provider before activating a connector. It must support an OAuth
authorization-code flow with PKCE and a client-registration approach accepted by the chosen client.
The host discovers keys using OIDC metadata at the configured issuer; it does not implement arbitrary
opaque-token introspection or a standalone OAuth metadata-only provider.

For each project, issue a token with this contract:

| Field | Required value |
|---|---|
| Signature and header | RS256 signature from a trusted issuer key; `typ` is `at+jwt` |
| `iss` | Exactly the configured issuer |
| `sub` | Exactly the configured owner's subject |
| `aud` | One value: `https://memory.example/projects/my-project/mcp` |
| Lifetime | Signed expiration and a currently valid lifetime; 30-second clock skew |
| `scope` | One space-delimited string containing `memory:read`; add `memory:write` for mutations |

An ID token, generic `JWT` type, unsigned token, another algorithm, another project, a trailing-slash
audience or multiple audiences is rejected. This deliberately narrow token profile needs issuer
configuration; support for OAuth alone does not establish compatibility. Validation uses
[ASP.NET JWT bearer authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0).

For Keycloak 26.8.0, the validated setup uses the client attribute
`access.token.header.type.rfc9068=true`, RS256, an explicit project audience mapper, and a
`basic` client scope containing the `oidc-sub-mapper`. Omitting the subject mapper produces a token
that this host correctly rejects. Give read clients `memory:read`; give write clients both memory
scopes and require consent. The `basic` scope need not appear in the token's scope string.
See Keycloak's [access-token header setting](https://www.keycloak.org/docs/latest/release_notes/index.html#new-client-configuration-for-access-token-header-type)
and [subject mapper documentation](https://www.keycloak.org/docs/26.7.5/server_admin/index.html).
Use separate registered clients per project unless the issuer can enforce the requested resource;
the tested static audience mapper does not implement arbitrary resource selection.

Resource metadata is public at `/.well-known/oauth-protected-resource/projects/my-project/mcp`.
Unauthenticated MCP requests return a 401 Bearer challenge linking to that metadata. The metadata and
challenge request read scope, plus write scope when server writes are enabled. The provider must honor
the requested project resource and issue the matching audience; registering each resource may be necessary.

Read-only tokens expose `recall`, `history`, `profile` and `write_status`. `remember` and `forget`
are listed only with both server write opt-in and token write scope. Calling a hidden mutation directly
still fails with `insufficient_scope` inside the MCP tool result. Changing permissions does not change
job ownership. Profile reads can refresh the deterministic profile cache. Writes remain sourced queue
proposals, and forgetting remains non-destructive.

Known-project requests, including discovery and rejected tokens, share a fixed-window limit of 120
requests per minute per project, without a waiting queue. Excess requests return 429. An anonymous
caller can exhaust this small shared allowance; deployment-level traffic controls are still needed
for internet operation. No per-IP or token partition table grows from untrusted input. Access tokens
are accepted only in the Authorization header, bounded to 8 KiB. Browser Origins are rejected.

## Claude connector

Claude custom connectors connect from Anthropic's cloud. The service must be reachable there; a
loopback-only example is not a deployed connector. Claude supports its published OAuth client identity,
automatic registration, or a supplied OAuth client, depending on the provider. See
[Claude's connector guide](https://support.claude.com/en/articles/11176164-use-connectors-to-extend-claude-s-capabilities).

Use the project MCP URL as the connector URL. Complete authorization against the external issuer, then
verify discovery, sign-in, read-only tools and rejection of another project's token with synthetic data.
If enabling writes later, reconnect/reauthorize with write scope and verify acceptance, status and history.
The connector relies on model tool selection; it does not add coding hooks or guaranteed per-turn recall.
No connector is installed automatically. Live client use, provider data permissions and deployment
verification remain separate from the local protocol tests.

The existing queue is in-process and not durable. Token validation does not provide immediate token
revocation: use short-lived access tokens and the provider's session controls. This is not a multi-user
SaaS or a claim of production security review. The input filter is heuristic and cannot audit stored
graph data or prove that a response contains no secrets. Plan which project memories a client may receive.
