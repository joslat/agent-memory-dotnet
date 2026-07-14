# Security Policy

## Reporting a vulnerability

**Do not open a public GitHub issue for a security vulnerability.**

Use GitHub's private vulnerability reporting instead: go to the
[Security tab](https://github.com/joslat/agent-memory-dotnet/security) → **Advisories** → **Report a
vulnerability**. This opens a private draft advisory visible only to you and the maintainer, so the
issue isn't disclosed before a fix is available.

## Supported versions

This project follows SemVer. Only the latest published `1.x` release is actively supported; there is no
long-term-support branch at this stage.

## Scope and known design tradeoffs

Before reporting, it's worth reading the [threat model](docs/security/threat-model.md) — it documents,
in detail, which isolation/authorization properties currently hold, which hold only partially, and which
are open gaps with a stated mitigation plan. If what you found matches an already-documented gap there,
it's still useful to report (confirms real-world impact), but it isn't a surprise to the maintainer.

The [production checklist](docs/security/production-checklist.md) lists what a deployment is expected
to configure itself (Neo4j credentials, TLS, secrets storage, rate limiting, etc.) — AgentMemory does not
and cannot guarantee these on its own.

## Supply chain

Releases are published via OIDC trusted publishing (no long-lived NuGet API key), every third-party
GitHub Action is pinned to a commit SHA with Dependabot tracking updates, and published packages carry
build-provenance attestations. See [threat-model.md §5, TT-14](docs/security/threat-model.md) for detail.
