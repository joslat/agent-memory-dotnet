# Remote coding memory over HTTPS/OAuth

The coding host can act as a personal MCP resource server. It uses the same scoped memory operations,
writer and pre-provisioned project databases as [local coding mode](coding-service.md). The remote
transport is implemented and tested locally with synthetic TLS certificates, signing keys and memory
services. It has not been validated against a live authorization provider or Claude connector.

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
